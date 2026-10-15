using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CCWrapper.Core;

namespace CCWrapper.Data;

/// <summary>One live claude process from <c>sessions\&lt;pid&gt;.json</c> (alive pids only).</summary>
public sealed record RunningSession(
    int Pid, string SessionId, string? Cwd, string? Name, SessionStatus Status, string? WaitingFor, string? Kind,
    DateTimeOffset? UpdatedAt);

/// <summary>One transcript of an account. <see cref="Running"/> is set while a claude process has it open.</summary>
public sealed record SessionEntry(Account Account, string Id, string Path, string? Cwd, DateTimeOffset LastActivity, string Title)
{
    public RunningSession? Running { get; init; }
}

/// <param name="LastUsed">DateTimeOffset.MinValue when unknown.</param>
public sealed record RecentProject(string Path, DateTimeOffset LastUsed);

public static class SessionCatalog
{
    const int HeadBytes = 64 * 1024, TailBytes = 64 * 1024;

    record Probe(long Size, long Ticks, string? Cwd, string? AiTitle, string? LastPrompt);

    // Process-wide so repeated List calls stay cheap; keyed by full path, valid while (size, mtime) match.
    static readonly ConcurrentDictionary<string, Probe> Probes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All sessions of the account, newest first, with the running registry merged in.</summary>
    public static IReadOnlyList<SessionEntry> List(Account account)
    {
        var running = Running(account).GroupBy(r => r.SessionId).ToDictionary(g => g.Key, g => g.MaxBy(r => r.UpdatedAt)!);
        var history = new Lazy<Dictionary<string, string>>(() => HistoryTitles(account.Paths.History));
        var list = new List<SessionEntry>();
        foreach (var file in TranscriptFiles(account))
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            var probe = ProbeOf(file);
            var title = CustomTitle(file, id) ?? probe.AiTitle ?? probe.LastPrompt ?? history.Value.GetValueOrDefault(id) ?? id;
            list.Add(new SessionEntry(account, id, file.FullName, probe.Cwd, file.LastWriteTimeUtc, Collapse(title)) { Running = running.GetValueOrDefault(id) });
        }
        return list.OrderByDescending(s => s.LastActivity).ToList();
    }

    /// <summary>The newest sessions over all given accounts (the start screen's recent list).</summary>
    public static IReadOnlyList<SessionEntry> Recent(IEnumerable<Account> accounts, int take = 30) =>
        accounts.SelectMany(a => List(a)).OrderByDescending(s => s.LastActivity).Take(take).ToList();

    /// <summary>Live claude processes of the account: dead pids, and pids reused by another process, are dropped.</summary>
    public static IReadOnlyList<RunningSession> Running(Account account)
    {
        var dir = account.Paths.Sessions;
        if (!Directory.Exists(dir)) return [];
        var result = new List<RunningSession>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            using var doc = JsonUtil.Load(file, attempts: 1); // registry files are tiny and rewritten often: the next poll catches a torn one
            var r = doc?.RootElement ?? default;
            if (r.Prop("pid").Num() is not { } pid || r.Prop("sessionId").Str() is not { Length: > 0 } sessionId) continue;
            if (!Alive((int)pid, r.Prop("procStart").Str())) continue;
            var waiting = r.Prop("waitingFor");
            result.Add(new RunningSession(
                (int)pid, sessionId, r.Prop("cwd").Str(), r.Prop("name").Str(),
                Enum.TryParse<SessionStatus>(r.Prop("status").Str(), ignoreCase: true, out var status) ? status : SessionStatus.Unknown,
                waiting.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : waiting.Str() ?? waiting.GetRawText(),
                r.Prop("kind").Str(), JsonUtil.Ms(r.Prop("updatedAt"))));
        }
        return result;
    }

    /// <summary>Folders for the New-session dialog: .claude.json "projects" plus transcript cwds, most recent first. Folders that no longer exist are dropped.</summary>
    public static IReadOnlyList<RecentProject> RecentProjects(Account account)
    {
        var map = new Dictionary<string, RecentProject>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path, DateTimeOffset? at)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch (ArgumentException) { return; }
            var t = at ?? DateTimeOffset.MinValue;
            map[full] = map.TryGetValue(full, out var old) ? old with { LastUsed = old.LastUsed > t ? old.LastUsed : t } : new(full, t);
        }
        foreach (var s in List(account)) Add(s.Cwd, s.LastActivity);
        foreach (var (path, lastStart) in AccountReader.Projects(account)) Add(path, lastStart);
        return map.Values.Where(p => Directory.Exists(p.Path)).OrderByDescending(p => p.LastUsed).ToList();
    }

    /// <summary>Top-level projects\&lt;dir&gt;\&lt;id&gt;.jsonl files only; subagent transcripts sit deeper and are skipped.</summary>
    internal static IEnumerable<FileInfo> TranscriptFiles(Account account)
    {
        var root = new DirectoryInfo(account.Paths.Projects);
        return root.Exists ? root.EnumerateDirectories().SelectMany(d => d.EnumerateFiles("*.jsonl")) : [];
    }

    static Probe ProbeOf(FileInfo file)
    {
        var (size, ticks) = (file.Length, file.LastWriteTimeUtc.Ticks);
        if (Probes.TryGetValue(file.FullName, out var cached) && cached.Size == size && cached.Ticks == ticks) return cached;

        string? cwd = null, ai = null, prompt = null;
        try
        {
            foreach (var (line, next) in JsonlReader.ReadLines(file.FullName))
            {
                if (line.Contains("\"cwd\"") && JsonlReader.TryParse(line) is { } r && Text(r, "cwd") is { } c) { cwd = c; break; }
                if (next > HeadBytes) break;
            }
            // The first tail line is usually cut mid-record; it fails to parse and is skipped like any bad line.
            foreach (var (line, _) in JsonlReader.ReadLines(file.FullName, Math.Max(0, size - TailBytes)))
            {
                if (!line.Contains("\"ai-title\"") && !line.Contains("\"last-prompt\"")) continue;
                if (JsonlReader.TryParse(line) is not { } r) continue;
                switch (r.Prop("type").Str())
                {
                    case "ai-title": ai = Text(r, "aiTitle") ?? ai; break;
                    case "last-prompt": prompt = Text(r, "lastPrompt") ?? prompt; break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Probe(size, ticks, cwd, ai, prompt); // vanished or locked: show what we have, retry next time
        }
        return Probes[file.FullName] = new Probe(size, ticks, cwd, ai, prompt);
    }

    static string? CustomTitle(FileInfo transcript, string id)
    {
        var path = Path.Combine(transcript.DirectoryName!, id, "custom-title.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonUtil.Load(path, attempts: 1);
        return doc is null ? null : Text(doc.RootElement, "customTitle");
    }

    // First "display" per sessionId. shortcut: re-read on every List that needs it; cache by (size, mtime) if history.jsonl grows large.
    static Dictionary<string, string> HistoryTitles(string path)
    {
        var map = new Dictionary<string, string>();
        if (!File.Exists(path)) return map;
        try
        {
            foreach (var (json, _) in JsonlReader.Read(path))
                if (Text(json, "sessionId") is { } id && Text(json, "display") is { } display) map.TryAdd(id, display);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return map;
    }

    static string? Text(JsonElement e, string name) => e.Prop(name).Str() is { } s && !string.IsNullOrWhiteSpace(s) ? s : null;

    static string Collapse(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 200 ? s[..200] : s;
    }

    static bool Alive(int pid, string? procStart)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            // procStart is the process creation FILETIME: a different one means Windows reused the pid.
            return !long.TryParse(procStart, out var created) || Math.Abs(p.StartTime.ToFileTimeUtc() - created) < TimeSpan.TicksPerSecond;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; } // exists but not inspectable (elevated): trust the registry
    }
}
