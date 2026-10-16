using System.Globalization;
using System.IO;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;

namespace CCWrapper.Views;

/// <summary>Control-free helpers of the app shell (picker, top bar, sidebar), so they can be unit-tested.</summary>
public static class ShellModel
{
    // Fixed palette; the color of an account is picked by its letter (A blue, B amber, C green, ...).
    static readonly string[] Palette = ["#7AA2F7", "#E0AF68", "#9ECE6A", "#BB9AF7", "#F7768E", "#7DCFFF", "#FF9E64"];

    public static string ColorOf(string letter) =>
        Palette[letter.Length == 0 ? 0 : ((char.ToLowerInvariant(letter[0]) - 'a') % Palette.Length + Palette.Length) % Palette.Length];

    /// <summary>The avatar text: first character, upper-case.</summary>
    public static string LetterOf(string letter) => letter.Length == 0 ? "?" : char.ToUpperInvariant(letter[0]).ToString();

    /// <summary>oauthAccount.organizationType as a plan name: claude_team -> Team, claude_pro -> Pro.</summary>
    public static string? PlanOf(string? organizationType) => organizationType switch
    {
        null or "" => null,
        "claude_team" => "Team",
        "claude_pro" => "Pro",
        "claude_max" => "Max",
        "claude_enterprise" => "Enterprise",
        var other => other,
    };

    public static string Age(DateTimeOffset at, DateTimeOffset now) =>
        now - at is { TotalSeconds: >= 60 } age ? AgentRow.FormatAge(age) : "now";

    public static string AsOf(DateTimeOffset? at, DateTimeOffset now) =>
        at?.ToLocalTime() is not { } t ? "usage unknown"
        : t.Date == now.ToLocalTime().Date ? $"as of {t:HH:mm}" : $"as of {t:d MMM HH:mm}";

    public static string Percent(double? value) => value is { } v ? $"{v:0}%" : "—";

    public const double MinCardWidth = 120, MaxCardWidth = 220;

    /// <summary>A tab card's width: <paramref name="count"/> cards share <paramref name="available"/> px (2 px apart), between
    /// 120 and 220 px; below 120 the bar scrolls sideways instead.</summary>
    public static double CardWidth(double available, int count) =>
        Math.Clamp(available / Math.Max(1, count) - 2, MinCardWidth, MaxCardWidth);

    /// <summary>The top bar's git chip tooltip: who commits and pushes, and the branch against its upstream as of the last fetch.</summary>
    public static string GitTip(GitInfo info)
    {
        if (!info.InRepo) return "Not in a git repository";
        List<string> lines = [];
        if (info.UserName is not null || info.UserEmail is not null) lines.Add($"Commits as {info.UserName} <{info.UserEmail}>");
        lines.Add(info.PushAccount is { } account ? $"Pushes to GitHub as {account}" : "GitHub account unknown");
        if (info.Branch is { } branch)
            lines.Add(info.Upstream is { } upstream
                ? $"{branch} → {upstream}: {info.Ahead ?? 0} to push, {info.Behind ?? 0} to pull (as of the last fetch)"
                : $"{branch}: no upstream branch");
        return string.Join("\n", lines);
    }

    /// <summary>Last folder name of a cwd, "" when unknown.</summary>
    public static string ProjectOf(string? cwd) =>
        string.IsNullOrWhiteSpace(cwd) ? "" : Path.GetFileName(Path.TrimEndingDirectorySeparator(cwd)) is { Length: > 0 } name ? name : cwd;

    /// <summary>A typed or picked project folder as a full path without a trailing separator, so that <c>C:\proj\</c> and
    /// <c>C:\proj</c> both map to Claude Code's <c>C--proj</c> transcript folder. Unusable text is returned trimmed.</summary>
    public static string NormalizeFolder(string typed)
    {
        var path = typed.Trim().Trim('"');
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    /// <summary>The open session a claude process belongs to: by pid first (the tab spawned it; /clear and an in-TUI /resume
    /// change the session id but not the pid), then by session id. Exited sessions have no pid and match by id only.</summary>
    public static LiveSession? Match(IEnumerable<LiveSession> open, int? pid, string id) =>
        open.FirstOrDefault(s => pid is { } p && s.Pid == p) ?? open.FirstOrDefault(s => s.SessionId == id);

    /// <summary>A tab started through a launcher (a Scoop or Chocolatey shim) holds the launcher's pid, but Claude Code registers
    /// under its own. A running tab with no entry of its pid takes the one entry of its session id whose process its own
    /// process started, so pid matching and the fail-closed submit check work for it. <paramref name="parents"/> is read
    /// at most once, and only when a tab needs it.</summary>
    public static void Rebind(IEnumerable<LiveSession> open, IReadOnlyList<RunningSession> running, Func<IReadOnlyDictionary<int, int>> parents)
    {
        IReadOnlyDictionary<int, int>? tree = null;
        foreach (var s in open)
        {
            if (s.Status == SessionStatus.Exited || s.Pid is not { } pid || running.Any(r => r.Pid == pid)) continue;
            var started = running.Where(r => r.SessionId == s.SessionId).Where(r => ProcessTree.IsDescendant(tree ??= parents(), r.Pid, pid)).Take(2).ToList();
            if (started.Count == 1) s.Pid = started[0].Pid;
        }
    }

    /// <summary>The registry entry of every open session that has a live claude process; exited sessions are left alone.
    /// Per session its own pid's entry wins, else the newest entry of its session id.</summary>
    public static IEnumerable<(LiveSession Session, RunningSession Registry)> MapStatus(
        IEnumerable<LiveSession> open, IReadOnlyList<RunningSession> running)
    {
        var alive = open.Where(s => s.Status != SessionStatus.Exited).ToList();
        foreach (var g in running.Select(r => (Reg: r, Tab: Match(alive, r.Pid, r.SessionId))).Where(x => x.Tab is not null).GroupBy(x => x.Tab!))
            yield return (g.Key, g.OrderByDescending(x => x.Reg.Pid == g.Key.Pid).ThenByDescending(x => x.Reg.UpdatedAt).First().Reg);
    }

    /// <summary>A session whose registry entry carries another id (after /clear or /resume inside the TUI) follows it:
    /// new id, its transcript and the registry name as title.</summary>
    public static void Adopt(LiveSession session, RunningSession registry)
    {
        if (session.SessionId == registry.SessionId) return;
        session.SessionId = registry.SessionId;
        if (session.Cwd is { } cwd) session.TranscriptPath = session.Account.Paths.Transcript(cwd, registry.SessionId);
        if (registry.Name is { Length: > 0 } name) session.Title = name;
    }
}

/// <summary>One picker card. Utilization values are percents; 0 with a "—" text when unknown.</summary>
public sealed record AccountCard(Account Account, string Letter, string Color, string Title, string Subtitle,
    double FiveHour, string FiveHourText, double Week, string WeekText, string AsOf, string Running)
{
    public static AccountCard From(Account account, AccountInfo info, int running, DateTimeOffset now) => new(
        account, ShellModel.LetterOf(account.Letter), ShellModel.ColorOf(account.Letter),
        info.Email ?? account.Label,
        string.Join(" · ", new[] { info.DisplayName, ShellModel.PlanOf(info.OrganizationType) }.Where(s => !string.IsNullOrEmpty(s))),
        info.FiveHour?.Utilization ?? 0, ShellModel.Percent(info.FiveHour?.Utilization),
        info.SevenDay?.Utilization ?? 0, ShellModel.Percent(info.SevenDay?.Utilization),
        ShellModel.AsOf(info.UsageFetchedAt, now), running == 0 ? "none running" : $"{running} running");
}

/// <summary>One row of the picker's recent-sessions list.</summary>
public sealed record RecentRow(SessionEntry Entry, string Letter, string Color, string Title, string Project, string Age)
{
    public static RecentRow From(SessionEntry e, DateTimeOffset now) => new(
        e, ShellModel.LetterOf(e.Account.Letter), ShellModel.ColorOf(e.Account.Letter), e.Title, ShellModel.ProjectOf(e.Cwd), ShellModel.Age(e.LastActivity, now));
}

/// <summary>One row of the sidebar: a transcript of the account merged with the session open in this window.
/// Equality lets the sidebar leave unchanged rows alone when it refreshes.</summary>
/// <param name="Live">The tab in this window, or null.</param>
/// <param name="Entry">The transcript, or null for a session that has not written one yet.</param>
public sealed record SessionRow(string Key, SessionEntry? Entry, LiveSession? Live, string Title, string Project, string Age,
    string Dot, string DotColor, bool IsActive, string? Snippet, string Tip)
{
    // Entry is left out: its Running.UpdatedAt changes on every refresh of a busy session and nothing shows it, so
    // comparing it would rebuild the ListBoxItem (selection, tooltip, in-flight click). What the row shows is compared.
    public bool Equals(SessionRow? other) => other is not null && Key == other.Key && Live == other.Live && Title == other.Title
        && Project == other.Project && Age == other.Age && Dot == other.Dot && DotColor == other.DotColor && IsActive == other.IsActive
        && Snippet == other.Snippet && Tip == other.Tip;

    public override int GetHashCode() => Key.GetHashCode();

    public bool IsOpen => Live is not null;

    /// <summary>A claude process that is not a tab of this window (another window, PowerShell, Desktop).</summary>
    public bool IsElsewhere => Live is null && Entry?.Running is not null;

    /// <summary>The row's status label: "working", "needs you", "open", or "" when no claude process has it.</summary>
    public string Badge => Dot switch { "●" => "working", "◉" => "needs you", "○" => "open", _ => "" };

    /// <summary>"project · age", or just the age when the project is unknown.</summary>
    public string Meta => Project.Length == 0 ? Age : $"{Project} · {Age}";

    /// <summary>Open sessions plus the account's transcripts, newest first. <paramref name="hits"/> (full-text search)
    /// restricts the list to the matching transcripts and adds their snippet; otherwise <paramref name="filter"/> is
    /// matched against title and project.</summary>
    public static List<SessionRow> Build(IReadOnlyList<SessionEntry> catalog, IEnumerable<LiveSession> open, LiveSession? active,
        DateTimeOffset now, string? filter = null, IReadOnlyDictionary<string, SearchHit>? hits = null)
    {
        var live = new Dictionary<string, LiveSession>();
        foreach (var s in open) if (s.SessionId is { } id) live.TryAdd(id, s);

        var rows = new List<(DateTimeOffset At, SessionRow Row)>();
        var matched = new HashSet<LiveSession>(); // tabs that own a transcript row, by pid or id
        foreach (var e in catalog)
            if (hits is null || hits.ContainsKey(e.Id))
            {
                var tab = ShellModel.Match(open, e.Running?.Pid, e.Id);
                if (tab is not null) matched.Add(tab);
                rows.Add((e.LastActivity, Make(e, tab, e.Id, active, now, hits?.GetValueOrDefault(e.Id)?.Snippet)));
            }
        if (hits is null)
        {
            var known = catalog.Select(e => e.Id).ToHashSet();
            foreach (var (id, l) in live)
                if (!known.Contains(id) && !matched.Contains(l)) rows.Add((now, Make(null, l, id, active, now, null))); // no transcript yet: newest
        }

        var all = rows.OrderByDescending(r => r.At).Select(r => r.Row);
        return hits is not null || string.IsNullOrWhiteSpace(filter) ? all.ToList()
            : all.Where(r => r.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Project.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    static SessionRow Make(SessionEntry? entry, LiveSession? live, string id, LiveSession? active, DateTimeOffset now, string? snippet)
    {
        var cwd = entry?.Cwd ?? live?.Cwd;
        var hasProcess = (live is not null && live.Status != SessionStatus.Exited) || entry?.Running is not null;
        var status = live?.Status ?? entry?.Running?.Status ?? SessionStatus.Unknown;
        var waitingFor = live?.WaitingFor ?? entry?.Running?.WaitingFor;
        var (dot, color) = !hasProcess ? ("", "#9A9A9A") : status switch
        {
            SessionStatus.Busy => ("●", "#6FCF97"),
            SessionStatus.Waiting => ("◉", "#F2C94C"),
            _ => ("○", "#9A9A9A"),
        };
        var state = live is not null ? status == SessionStatus.Exited ? "Ended in this window" : "Open in this window"
            : entry?.Running is not null ? "Open in another terminal: shown read-only here" : "Closed: click to resume";
        if (status == SessionStatus.Waiting) state += $" · waiting for {waitingFor ?? "input"}";
        return new SessionRow(id, entry, live, entry?.Title ?? live?.Title ?? "New session", ShellModel.ProjectOf(cwd),
            entry is null ? "now" : ShellModel.Age(entry.LastActivity, now), dot, color, live is not null && live == active, snippet,
            string.Join("\n", new[] { cwd, state }.Where(s => !string.IsNullOrEmpty(s))));
    }
}
