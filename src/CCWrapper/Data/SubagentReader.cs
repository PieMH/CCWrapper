using System.IO;
using System.Text.Json;
using CCWrapper.Core;
using static CCWrapper.Data.TranscriptReader.Get;

namespace CCWrapper.Data;

/// <summary>One subagent of a session. Meta fields come from agent-*.meta.json, the numbers from its transcript.</summary>
/// <param name="ContextTokens">Last turn's input + cache creation + cache read tokens.</param>
public sealed record SubagentInfo(string AgentId, string? AgentType, string? Description, string? ToolUseId, string? RequestShape,
    string? Model, string? Effort, long? ContextTokens, long OutputTokens, int ToolUseCount, DateTimeOffset? LastActivity,
    string TranscriptPath, bool IsRunning);

/// <summary>The subagents of one session, from <c>&lt;session dir&gt;\subagents</c>. Call <see cref="Poll"/> on a timer.</summary>
public sealed class SubagentReader(string transcriptPath)
{
    static readonly TimeSpan RecentWrite = TimeSpan.FromSeconds(10);
    static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    readonly string _dir = Path.Combine(Path.GetDirectoryName(transcriptPath) ?? "", Path.GetFileNameWithoutExtension(transcriptPath), "subagents");
    readonly Dictionary<string, (JsonElement Meta, TranscriptReader Reader)> _agents = [];

    /// <summary>Running agents first, then newest activity first.</summary>
    public IReadOnlyList<SubagentInfo> Agents { get; private set; } = [];

    /// <summary>Reads new meta files and transcript lines. True if <see cref="Agents"/> now differs (running/done flips with the clock too).</summary>
    public bool Poll()
    {
        var infos = new List<SubagentInfo>();
        foreach (var metaPath in SafeMetaFiles())
        {
            if (!_agents.TryGetValue(metaPath, out var agent))
            {
                if (ReadMeta(metaPath) is not { } meta) continue; // being written: next poll
                var jsonl = metaPath[..^".meta.json".Length] + ".jsonl";
                _agents[metaPath] = agent = (meta, new TranscriptReader(jsonl));
            }
            agent.Reader.Poll();
            infos.Add(Describe(metaPath, agent.Meta, agent.Reader));
        }
        infos = [.. infos.OrderByDescending(i => i.IsRunning).ThenByDescending(i => i.LastActivity).ThenBy(i => i.AgentId, StringComparer.Ordinal)];
        var changed = !infos.SequenceEqual(Agents);
        Agents = infos;
        return changed;
    }

    IEnumerable<string> SafeMetaFiles()
    {
        try { return Directory.Exists(_dir) ? Directory.GetFiles(_dir, "agent-*.meta.json") : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    static JsonElement? ReadMeta(string path)
    {
        try
        {
            using var stream = JsonlReader.OpenShared(path);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    static SubagentInfo Describe(string metaPath, JsonElement meta, TranscriptReader r)
    {
        var id = Path.GetFileName(metaPath)["agent-".Length..^".meta.json".Length];
        // the file's mtime, or the meta's while the transcript is not created yet
        var stamp = File.Exists(r.Path) ? r.Path : metaPath;
        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp);
        // shortcut: no link to the claude.exe process, so "running" is a guess: written within 10 s, or the last assistant
        // stop_reason is not end_turn (agents killed mid-turn never write one, hence the 30 min cap); upgrade with a pid check.
        var running = age < RecentWrite || (r.LastStopReason != "end_turn" && age < StaleAfter);
        return new SubagentInfo(id, Str(meta, "agentType"), Str(meta, "description"), Str(meta, "toolUseId"), Str(meta, "requestShape"),
            r.Model ?? Str(meta, "model"), r.Effort ?? Str(meta, "effort"), r.ContextTokens, r.OutputTokens, r.ToolUseCount,
            r.LastActivity, r.Path, running);
    }
}
