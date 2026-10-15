using System.IO;
using System.Text;
using System.Text.Json;
using CCWrapper.Core;
using DiffPlex;
using static CCWrapper.Data.TranscriptReader.Get;

namespace CCWrapper.Data;

public enum ChangeKind { Added, Modified, Deleted }

/// <summary>One file Claude changed in a session. Added/Removed are null for binary files and when the base backup is gone;
/// sizes are null when unknown (a missing side counts as 0 bytes only for new and deleted files).</summary>
/// <param name="RelPath">Relative to the session cwd, or the full path for files outside it.</param>
public sealed record FileChange(string RelPath, string FullPath, ChangeKind Kind, bool IsBinary,
    int? Added, int? Removed, long? BaseSize, long? CurrentSize, string? BackupPath)
{
    /// <summary>The text before Claude's first edit: "" for a new file, null for binary or when the backup is missing.</summary>
    public string? BaseText() => IsBinary ? null : Kind == ChangeKind.Added ? "" : BackupPath is null ? null : FileChanges.TryReadText(BackupPath);

    /// <summary>The text on disk now: "" for a deleted file, null for binary or when unreadable.</summary>
    public string? CurrentText() => IsBinary ? null : Kind == ChangeKind.Deleted ? "" : FileChanges.TryReadText(FullPath);
}

/// <summary>Files Claude changed in one session: Claude's checkpoint backup (before its first edit) versus the file on disk.
/// Only sees edits made with Claude's own Edit/Write tools. Call <see cref="Refresh"/> to pick up appended records and disk changes.</summary>
public sealed class FileChanges(ClaudePaths paths, string transcriptPath, string cwd)
{
    record struct Base(int Version, string? BackupFileName);
    record struct Stamp(string? Backup, bool BackupExists, long Length, long Ticks);

    readonly string _historyDir = Path.Combine(paths.FileHistory, Path.GetFileNameWithoutExtension(transcriptPath));
    readonly Dictionary<string, Base> _bases = []; // tracked path as recorded -> earliest version
    readonly Dictionary<string, (Stamp Stamp, FileChange? Change)> _cache = [];
    long _offset;

    public IReadOnlyList<FileChange> Refresh()
    {
        ReadRecords();
        var changes = new List<FileChange>();
        foreach (var (tracked, b) in _bases)
            if (Evaluate(tracked, b) is { } c) changes.Add(c);
        return changes.OrderBy(c => c.RelPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    void ReadRecords()
    {
        try
        {
            foreach (var (json, next) in JsonlReader.Read(transcriptPath, _offset))
            {
                _offset = next;
                switch (Str(json, "type"))
                {
                    case "file-history-snapshot":
                        if (Prop(Prop(json, "snapshot"), "trackedFileBackups") is { ValueKind: JsonValueKind.Object } tracked)
                            foreach (var p in tracked.EnumerateObject()) Track(p.Name, p.Value);
                        break;
                    case "file-history-delta":
                        if (Str(json, "trackingPath") is { } path && Prop(json, "backup") is { } backup) Track(path, backup);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // transcript gone or locked: keep what we have
        }
    }

    // The earliest version is the file as it was before Claude's first edit; null name = the file did not exist.
    void Track(string path, JsonElement backup)
    {
        var version = (int)(Num(backup, "version") ?? 0);
        if (!_bases.TryGetValue(path, out var known) || version < known.Version)
            _bases[path] = new Base(version, Str(backup, "backupFileName"));
    }

    FileChange? Evaluate(string tracked, Base b)
    {
        string full;
        try { full = Path.GetFullPath(tracked, cwd); }
        catch (ArgumentException) { return null; } // malformed path in the record

        var backup = b.BackupFileName is null ? null : Path.Combine(_historyDir, b.BackupFileName);
        var current = new FileInfo(full);
        var stamp = new Stamp(backup, backup is not null && File.Exists(backup), current.Exists ? current.Length : -1, current.Exists ? current.LastWriteTimeUtc.Ticks : 0);
        if (_cache.TryGetValue(full, out var hit) && hit.Stamp == stamp) return hit.Change;

        FileChange? change;
        try { change = Compare(full, backup, stamp); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } // not cached: retry next time
        _cache[full] = (stamp, change);
        return change;
    }

    FileChange? Compare(string full, string? backup, Stamp stamp)
    {
        var exists = stamp.Length >= 0;
        var isNew = backup is null;
        if (isNew && !exists) return null; // created and removed again: nothing changed
        var rel = RelativeTo(full);
        var kind = isNew ? ChangeKind.Added : exists ? ChangeKind.Modified : ChangeKind.Deleted;
        long? baseSize = isNew ? 0 : stamp.BackupExists ? new FileInfo(backup!).Length : null;
        long? currentSize = exists ? stamp.Length : 0;

        if (!isNew && !stamp.BackupExists) // checkpoint was cleaned up: we know the file is tracked, not what it was
            return new FileChange(rel, full, kind, exists && HasNul(full), null, null, null, currentSize, backup);

        var binary = (!isNew && HasNul(backup!)) || (exists && HasNul(full));
        if (binary)
        {
            var same = baseSize == currentSize && (isNew || ReadShared(backup!).AsSpan().SequenceEqual(ReadShared(full)));
            return same ? null : new FileChange(rel, full, kind, true, null, null, baseSize, currentSize, backup);
        }

        var before = isNew ? "" : TryReadText(backup!) ?? "";
        var after = exists ? TryReadText(full) ?? "" : "";
        if (before == after) return null;
        int added = 0, removed = 0;
        foreach (var block in new Differ().CreateLineDiffs(NoFinalNewline(before), NoFinalNewline(after), ignoreWhitespace: false).DiffBlocks)
        {
            removed += block.DeleteCountA;
            added += block.InsertCountB;
        }
        return new FileChange(rel, full, kind, false, added, removed, baseSize, currentSize, backup);
    }

    // DiffPlex counts the empty text after a final newline as a line; git does not.
    static string NoFinalNewline(string s) => s.EndsWith("\r\n") ? s[..^2] : s.EndsWith('\n') || s.EndsWith('\r') ? s[..^1] : s;

    string RelativeTo(string full)
    {
        var rel = Path.GetRelativePath(cwd, full);
        return rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(rel) ? full : rel;
    }

    // shortcut: a NUL in the first 8 KB means binary (git's rule); a UTF-16 text file would count as binary.
    static bool HasNul(string path)
    {
        using var s = JsonlReader.OpenShared(path);
        var buf = new byte[8192];
        return Array.IndexOf(buf, (byte)0, 0, s.Read(buf, 0, buf.Length)) >= 0;
    }

    // Not File.ReadAllBytes: it takes FileShare.Read and would block Claude (or the user) writing the file.
    static byte[] ReadShared(string path)
    {
        using var s = JsonlReader.OpenShared(path);
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    internal static string? TryReadText(string path)
    {
        try
        {
            using var reader = new StreamReader(JsonlReader.OpenShared(path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
