using System.Collections.Concurrent;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using CCWrapper.Core;

namespace CCWrapper.Data;

/// <param name="Count">Messages (user/assistant) whose text contains the query.</param>
/// <param name="Snippet">~120 chars around the first match, whitespace collapsed.</param>
public sealed record SearchHit(string SessionId, string Path, int Count, string Snippet);

public static class SessionSearch
{
    // Claude Code writes JSON.stringify output: only quotes, backslashes and control chars are escaped, non-ASCII stays raw.
    static readonly JsonSerializerOptions RawEscaping = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Full text over the account's transcripts, newest session first. Throws OperationCanceledException when cancelled.</summary>
    public static IReadOnlyList<SearchHit> SearchAccount(Account account, string query, CancellationToken ct = default)
    {
        var hits = new ConcurrentBag<(SearchHit Hit, DateTime Mtime)>();
        Parallel.ForEach(SessionCatalog.TranscriptFiles(account), new ParallelOptions { CancellationToken = ct }, file =>
        {
            if (SearchTranscript(file.FullName, query, ct) is { } hit) hits.Add((hit, file.LastWriteTimeUtc));
        });
        return hits.OrderByDescending(h => h.Mtime).Select(h => h.Hit).ToList();
    }

    /// <summary>Null when the query is blank or nothing matches.</summary>
    public static SearchHit? SearchTranscript(string path, string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var needle = JsonSerializer.Serialize(query, RawEscaping)[1..^1]; // the query as it appears inside a raw JSONL line
        int count = 0;
        string? snippet = null;
        try
        {
            foreach (var (line, _) in JsonlReader.ReadLines(path))
            {
                ct.ThrowIfCancellationRequested();
                if (!line.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue; // cheap prefilter before any JSON parsing
                if (JsonlReader.TryParse(line) is not { } record || record.Prop("type").Str() is not ("user" or "assistant")) continue;
                foreach (var text in Texts(record.Prop("message").Prop("content")))
                    if (text.IndexOf(query, StringComparison.OrdinalIgnoreCase) is >= 0 and var at)
                    {
                        count++;
                        snippet ??= Snippet(text, at);
                        break;
                    }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // file vanished mid-search: keep what was found
        return count == 0 ? null : new SearchHit(Path.GetFileNameWithoutExtension(path), path, count, snippet!);
    }

    // Message text only: a string content, or {type:"text"} items. Tool calls, tool results and thinking are not searched.
    static IEnumerable<string> Texts(JsonElement content)
    {
        if (content.Str() is { } s) yield return s;
        else if (content.ValueKind == JsonValueKind.Array)
            foreach (var item in content.EnumerateArray())
                if (item.Prop("type").Str() == "text" && item.Prop("text").Str() is { } t) yield return t;
    }

    static string Snippet(string text, int at)
    {
        var start = Math.Max(0, at - 30);
        var length = Math.Min(120, text.Length - start);
        var body = Regex.Replace(text.Substring(start, length), @"\s+", " ").Trim();
        return (start > 0 ? "…" : "") + body + (start + length < text.Length ? "…" : "");
    }
}
