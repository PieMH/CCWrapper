using System.Globalization;
using System.IO;
using System.Text.Json;
using CCWrapper.Core;
using static CCWrapper.Data.TranscriptReader.Get;

namespace CCWrapper.Data;

/// <summary>One message of the read-only transcript view. Tool results and thinking are left out.</summary>
/// <param name="Role">"user" or "assistant".</param>
/// <param name="ToolUses">One line each: "Edit: src\a.cs", "Bash: git status".</param>
public sealed record TranscriptMessage(string Role, DateTimeOffset? Time, string Text, IReadOnlyList<string> ToolUses);

/// <summary>Incremental tail of one session transcript (or subagent) .jsonl. Every value is null/empty until a record sets it.
/// Not thread-safe: call <see cref="Poll"/> from one thread, read the properties from the same one.</summary>
public sealed class TranscriptReader(string path)
{
    long _offset;
    readonly Dictionary<string, int> _indexById = []; // CC writes one record per content block of an API message, not always back to back
    readonly Dictionary<string, long> _outputById = [];
    readonly List<TranscriptMessage> _messages = [];

    public string Path { get; } = path;
    public string? Model { get; private set; }
    public string? Effort { get; private set; }
    public string? PermissionMode { get; private set; }
    public string? AiTitle { get; private set; }

    /// <summary>Last assistant turn: input + cache creation + cache read tokens.</summary>
    public long? ContextTokens { get; private set; }
    /// <summary>Sum over API messages of their output tokens.</summary>
    public long OutputTokens { get; private set; }
    public double? CostUsd { get; private set; }
    public long? LinesAdded { get; private set; }
    public long? LinesRemoved { get; private set; }

    /// <summary>stop_reason of the last assistant record; null while streaming or before any.</summary>
    public string? LastStopReason { get; private set; }
    public int ToolUseCount { get; private set; }
    /// <summary>Timestamp of the last record that has one.</summary>
    public DateTimeOffset? LastActivity { get; private set; }
    public IReadOnlyList<TranscriptMessage> Messages => _messages;

    /// <summary>Reads what was appended since the last call. True if any known record type arrived.
    /// A missing or locked file, or a malformed line, changes nothing.</summary>
    public bool Poll()
    {
        var changed = false;
        try
        {
            foreach (var (json, next) in JsonlReader.Read(Path, _offset))
            {
                changed |= Apply(json);
                _offset = next;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // file gone or briefly locked: keep the state, retry on the next poll
        }
        return changed;
    }

    bool Apply(JsonElement rec)
    {
        if (TimeOf(rec) is { } when) LastActivity = when;

        switch (Str(rec, "type"))
        {
            case "permission-mode":
                PermissionMode = Str(rec, "permissionMode") ?? PermissionMode;
                return true;
            case "ai-title":
                AiTitle = Str(rec, "aiTitle") ?? AiTitle;
                return true;
            case "cost-state":
                CostUsd = Num(rec, "totalCostUSD") ?? CostUsd;
                LinesAdded = (long?)Num(rec, "totalLinesAdded") ?? LinesAdded;
                LinesRemoved = (long?)Num(rec, "totalLinesRemoved") ?? LinesRemoved;
                return true;
            case "assistant":
                ApplyAssistant(rec);
                return true;
            case "user":
                ApplyUser(rec);
                return true;
            default:
                return false;
        }
    }

    void ApplyAssistant(JsonElement rec)
    {
        if (Prop(rec, "message") is not { } msg) return;
        var model = Str(msg, "model");
        // synthetic records are CC's own error stubs: no model, no usage
        if (model != "<synthetic>")
        {
            Model = model ?? Model;
            Effort = Str(rec, "effort") ?? Effort;
            LastStopReason = Str(msg, "stop_reason");
            if (Prop(msg, "usage") is { ValueKind: JsonValueKind.Object } usage)
            {
                ContextTokens = (long)((Num(usage, "input_tokens") ?? 0) + (Num(usage, "cache_creation_input_tokens") ?? 0) + (Num(usage, "cache_read_input_tokens") ?? 0));
                if (Num(usage, "output_tokens") is { } outTokens) AddOutput(Str(msg, "id"), (long)outTokens);
            }
        }

        var texts = new List<string>();
        var tools = new List<string>();
        if (Prop(msg, "content") is { ValueKind: JsonValueKind.Array } blocks)
            foreach (var b in blocks.EnumerateArray())
                switch (Str(b, "type"))
                {
                    case "text" when Str(b, "text") is { Length: > 0 } text:
                        texts.Add(text);
                        break;
                    case "tool_use":
                        ToolUseCount++;
                        tools.Add(ToolSummary(b));
                        break;
                }
        AddMessage("assistant", rec, Str(msg, "id"), texts, tools);
    }

    void AddOutput(string? messageId, long tokens)
    {
        if (messageId is null) { OutputTokens += tokens; return; }
        _outputById.TryGetValue(messageId, out var before);
        if (tokens > before) { _outputById[messageId] = tokens; OutputTokens += tokens - before; }
    }

    void ApplyUser(JsonElement rec)
    {
        if (Prop(rec, "isMeta") is { ValueKind: JsonValueKind.True } || Prop(rec, "isCompactSummary") is { ValueKind: JsonValueKind.True }) return;
        var texts = new List<string>();
        var content = Prop(Prop(rec, "message"), "content");
        if (content is { ValueKind: JsonValueKind.String } s && s.GetString() is { Length: > 0 } str) texts.Add(str);
        else if (content is { ValueKind: JsonValueKind.Array } blocks)
            foreach (var b in blocks.EnumerateArray())
                switch (Str(b, "type"))
                {
                    case "text" when Str(b, "text") is { Length: > 0 } text: texts.Add(text); break;
                    case "image": texts.Add("[image]"); break;
                }
        AddMessage("user", rec, null, texts, []);
    }

    /// <summary>Adds a message, or folds it into the earlier one when it is another block of the same API message.</summary>
    void AddMessage(string role, JsonElement rec, string? messageId, List<string> texts, List<string> tools)
    {
        if (texts.Count == 0 && tools.Count == 0) return;
        var text = string.Join("\n\n", texts);
        if (messageId is not null && _indexById.TryGetValue(messageId, out var i))
        {
            var known = _messages[i];
            _messages[i] = known with { Text = known.Text.Length == 0 ? text : text.Length == 0 ? known.Text : known.Text + "\n\n" + text, ToolUses = [.. known.ToolUses, .. tools] };
            return;
        }
        if (messageId is not null) _indexById[messageId] = _messages.Count;
        _messages.Add(new TranscriptMessage(role, TimeOf(rec), text, tools));
    }

    static DateTimeOffset? TimeOf(JsonElement rec) =>
        Str(rec, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    static string ToolSummary(JsonElement toolUse)
    {
        var name = Str(toolUse, "name") ?? "tool";
        var input = Prop(toolUse, "input");
        string? detail = null;
        foreach (var key in new[] { "file_path", "command", "pattern", "description" })
            if (Str(input, key) is { Length: > 0 } v) { detail = v; break; }
        if (detail is null) return name;
        var line = detail.Split('\n', 2)[0].Trim();
        return $"{name}: {(line.Length > 120 ? line[..120] + "…" : line)}";
    }

    /// <summary>Null-safe reads of Claude Code's undocumented JSON: a wrong or missing shape is just null.
    /// Plain static methods on purpose: extension methods here would clash with other Data helpers.</summary>
    internal static class Get
    {
        public static JsonElement? Prop(JsonElement? e, string name) =>
            e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) ? v : null;

        public static string? Str(JsonElement? e, string name) =>
            Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

        public static double? Num(JsonElement? e, string name) =>
            Prop(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) ? d : null;
    }
}
