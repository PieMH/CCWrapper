using System.IO;
using System.Text;
using System.Text.Json;

namespace CCWrapper.Core;

/// <summary>Reads JSONL files that Claude Code may be writing right now. Bad lines are skipped, never thrown.</summary>
public static class JsonlReader
{
    /// <summary>Opens for reading without blocking CC's appends, renames or deletes.</summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Raw lines from byte <paramref name="offset"/>, each with the byte offset just past it.
    /// The last line is yielded even without its trailing '\n'. A file shorter than offset yields nothing.</summary>
    public static IEnumerable<(string Text, long Next)> ReadLines(string path, long offset = 0)
    {
        using var stream = OpenShared(path);
        stream.Position = Math.Min(offset, stream.Length);
        var pending = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long chunkStart = stream.Position;
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            int start = 0, nl;
            while ((nl = Array.IndexOf(chunk, (byte)'\n', start, n - start)) >= 0)
            {
                pending.Write(chunk, start, nl - start);
                yield return (Decode(pending), chunkStart + nl + 1);
                pending.SetLength(0);
                start = nl + 1;
            }
            pending.Write(chunk, start, n - start);
            chunkStart += n;
        }
        if (pending.Length > 0) yield return (Decode(pending), chunkStart);
    }

    /// <summary>Parsed lines from <paramref name="offset"/>. To tail a live file, keep the last yielded Next:
    /// a half-written last line does not parse, is not yielded, and is read again next time.</summary>
    public static IEnumerable<(JsonElement Json, long Next)> Read(string path, long offset = 0)
    {
        foreach (var (text, next) in ReadLines(path, offset))
            if (TryParse(text) is { } json) yield return (json, next);
    }

    /// <summary>The line as a standalone JsonElement, or null if blank or invalid.</summary>
    public static JsonElement? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string Decode(MemoryStream s) =>
        Encoding.UTF8.GetString(s.GetBuffer(), 0, (int)s.Length).TrimStart('﻿').TrimEnd('\r');
}
