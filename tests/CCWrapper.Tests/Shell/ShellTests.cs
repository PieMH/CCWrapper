using System.Collections;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;
using CCWrapper.Views;

namespace CCWrapper.Tests.Shell;

public sealed class ShellTests
{
    static readonly Account A = new("a", null, @"C:\h\.claude.json", "a@x.com");
    static readonly Account C = new("c", @"C:\h\.claude-c", @"C:\h\.claude-c\.claude.json", "c@x.com");
    static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Local));

    static SessionEntry Entry(string id, string title, string cwd, TimeSpan ago, RunningSession? running = null) =>
        new(C, id, $@"C:\h\{id}.jsonl", cwd, Now - ago, title) { Running = running };

    static RunningSession Reg(string id, SessionStatus status, string? waitingFor = null, long updated = 0, int pid = 1, string? name = null) =>
        new(pid, id, @"C:\p", name, status, waitingFor, "interactive", DateTimeOffset.FromUnixTimeSeconds(updated));

    static LiveSession Live(string id, SessionStatus status = SessionStatus.Idle, int? pid = null) =>
        new(C) { SessionId = id, Cwd = @"C:\p\open", Status = status, Pid = pid };

    [Fact]
    public void Account_colors_are_fixed_by_letter_and_survive_odd_letters()
    {
        Assert.Equal(ShellModel.ColorOf("a"), ShellModel.ColorOf("A"));
        Assert.Equal(3, new[] { "a", "b", "c" }.Select(ShellModel.ColorOf).Distinct().Count());
        Assert.NotNull(ShellModel.ColorOf(""));
        Assert.NotNull(ShellModel.ColorOf("7"));
        Assert.Equal("W", ShellModel.LetterOf("work"));
    }

    [Theory]
    [InlineData("claude_team", "Team")]
    [InlineData("claude_pro", "Pro")]
    [InlineData("claude_max", "Max")]
    [InlineData("other", "other")]
    [InlineData(null, null)]
    public void Plan_names(string? type, string? plan) => Assert.Equal(plan, ShellModel.PlanOf(type));

    [Theory]
    [InlineData("●", "working")]
    [InlineData("◉", "needs you")]
    [InlineData("○", "open")]
    [InlineData("", "")]
    public void Session_row_badge_follows_its_status_dot(string dot, string badge) =>
        Assert.Equal(badge, new SessionRow("k", null, null, "t", "proj", "2m", dot, "#fff", false, null, "").Badge);

    [Fact]
    public void Session_row_meta_skips_an_unknown_project()
    {
        Assert.Equal("proj · 2m", new SessionRow("k", null, null, "t", "proj", "2m", "", "#fff", false, null, "").Meta);
        Assert.Equal("2m", new SessionRow("k", null, null, "t", "", "2m", "", "#fff", false, null, "").Meta);
    }

    [Theory]
    [InlineData("powershell", "PowerShell")]
    [InlineData("pwsh", "PowerShell")]
    [InlineData("CCWrapper", "a CCWrapper window")]
    [InlineData("WindowsTerminal", "Windows Terminal")]
    [InlineData("nu", "nu")]
    [InlineData(null, "another terminal")]
    public void Host_names_are_what_the_user_calls_the_program(string? process, string name) =>
        Assert.Equal(name, ProcessTree.HostName(process));

    [Fact]
    public void Describe_names_a_live_process_by_its_parent_with_pid_and_start_time()
    {
        var text = ProcessTree.Describe(Environment.ProcessId); // the test host, started by dotnet or an IDE
        Assert.Contains($"(pid {Environment.ProcessId}, since ", text);
        Assert.Equal("another terminal (pid 3)", ProcessTree.Describe(3)); // no such process: Windows pids are multiples of 4
    }

    [Fact]
    public void Git_tip_names_the_committer_the_push_account_and_the_branch_state()
    {
        Assert.Equal("Not in a git repository", ShellModel.GitTip(GitInfo.None));
        Assert.Equal("Commits as Me <me@x.com>\nPushes to GitHub as octocat\nmain → origin/main: 2 to push, 0 to pull (as of the last fetch)",
            ShellModel.GitTip(new GitInfo(true, "octocat", "Me", "me@x.com", "main", "origin/main", 2, 0)));
        Assert.Equal("GitHub account unknown\nwip: no upstream branch", ShellModel.GitTip(new GitInfo(true, null, null, null, "wip")));
    }

    [Fact]
    public void Age_is_now_under_a_minute_then_short_units()
    {
        Assert.Equal("now", ShellModel.Age(Now - TimeSpan.FromSeconds(30), Now));
        Assert.Equal("now", ShellModel.Age(Now + TimeSpan.FromSeconds(5), Now)); // clock skew
        Assert.Equal("2m", ShellModel.Age(Now - TimeSpan.FromMinutes(2), Now));
        Assert.Equal("3d", ShellModel.Age(Now - TimeSpan.FromDays(3), Now));
    }

    [Fact]
    public void AsOf_shows_the_time_today_and_the_date_otherwise()
    {
        var today = new DateTimeOffset(new DateTime(2026, 10, 15, 8, 5, 0, DateTimeKind.Local));
        Assert.Equal("as of 08:05", ShellModel.AsOf(today, Now));
        Assert.Contains("08:05", ShellModel.AsOf(today.AddDays(-5), Now));
        Assert.DoesNotContain("as of 08:05", ShellModel.AsOf(today.AddDays(-5), Now));
        Assert.Equal("usage unknown", ShellModel.AsOf(null, Now));
    }

    [Fact]
    public void Card_shows_email_plan_usage_and_running_count()
    {
        var info = new AccountInfo("me@x.com", "Me", "Org", "claude_team", null, Now, new(58, null), new(12, null), null, null, null);
        var card = AccountCard.From(C, info, 2, Now);
        Assert.Equal(("me@x.com", "Me · Team", "58%", "12%", "2 running", "C"), (card.Title, card.Subtitle, card.FiveHourText, card.WeekText, card.Running, card.Letter));

        var unknown = AccountCard.From(A, new AccountInfo(null, null, null, null, null, null, null, null, null, null, null), 0, Now);
        Assert.Equal(("a@x.com", "", "—", "usage unknown", "none running"), (unknown.Title, unknown.Subtitle, unknown.FiveHourText, unknown.AsOf, unknown.Running));
    }

    [Fact]
    public void MapStatus_falls_back_to_the_session_id_and_skips_exited()
    {
        var busy = Live("s1");
        var exited = Live("s2", SessionStatus.Exited);
        var unregistered = Live("s3");
        var result = ShellModel.MapStatus([busy, exited, unregistered],
            [Reg("s1", SessionStatus.Waiting, "permission", updated: 1), Reg("s1", SessionStatus.Busy, updated: 5, pid: 2), Reg("s2", SessionStatus.Busy)]).ToList();

        var (session, registry) = Assert.Single(result); // no pid known: the newest registry entry of s1 wins
        Assert.Equal((busy, SessionStatus.Busy, (string?)null), (session, registry.Status, registry.WaitingFor));
    }

    [Fact]
    public void MapStatus_matches_by_pid_when_the_session_id_changed_and_adopts_the_new_id()
    {
        var tab = Live("old", SessionStatus.Busy, pid: 10);
        tab.TranscriptPath = C.Paths.Transcript(@"C:\p\open", "old");
        var other = Live("other", pid: 11);
        var cleared = Reg("new", SessionStatus.Waiting, "permission", pid: 10, name: "After clear");

        var (session, registry) = Assert.Single(ShellModel.MapStatus([tab, other], [cleared, Reg("new", SessionStatus.Idle, updated: 9, pid: 99)]));
        Assert.Same(tab, session);
        Assert.Equal(10, registry.Pid); // its own pid wins over a newer entry of the same id from another process

        ShellModel.Adopt(session, registry);
        Assert.Equal(("new", C.Paths.Transcript(@"C:\p\open", "new"), "After clear"), (tab.SessionId, tab.TranscriptPath, tab.Title));
        Assert.Equal("other", other.SessionId);
    }

    [Fact]
    public void Adopt_leaves_a_session_with_the_same_id_alone()
    {
        var tab = Live("s1", pid: 10);
        tab.Title = "Mine";
        ShellModel.Adopt(tab, Reg("s1", SessionStatus.Idle, pid: 10, name: "Registry name"));
        Assert.Equal(("s1", "Mine", null), (tab.SessionId, tab.Title, tab.TranscriptPath));
    }

    [Fact]
    public void MapStatus_skips_unmatched_registry_entries_and_exited_tabs_without_a_pid()
    {
        var exited = Live("old", SessionStatus.Exited); // Pid is null after exit: only the id could match
        Assert.Empty(ShellModel.MapStatus([exited], [Reg("new", SessionStatus.Busy, pid: 10), Reg("old", SessionStatus.Busy, pid: 11)]));
        Assert.Empty(ShellModel.MapStatus([Live("s1", pid: 10)], [Reg("other", SessionStatus.Busy, pid: 11)]));
    }

    [Fact]
    public void Rebind_moves_a_launcher_tab_to_the_claude_process_it_started()
    {
        var shim = Live("s1", pid: 10);
        ShellModel.Rebind([shim], [Reg("s1", SessionStatus.Idle, pid: 30)], () => new Dictionary<int, int> { [30] = 20, [20] = 10 });
        Assert.Equal(30, shim.Pid); // grandchild through a second launcher
    }

    [Fact]
    public void Rebind_leaves_tabs_alone_unless_exactly_one_started_entry_has_their_session_id()
    {
        var tree = new Dictionary<int, int> { [20] = 10, [21] = 10, [40] = 5 };
        var unrelated = Live("s1", pid: 10);
        ShellModel.Rebind([unrelated], [Reg("s1", SessionStatus.Idle, pid: 40)], () => tree);
        var nested = Live("s1", pid: 10); // a claude run from the tab's own Bash tool has another session id
        ShellModel.Rebind([nested], [Reg("other", SessionStatus.Idle, pid: 20)], () => tree);
        var ambiguous = Live("s1", pid: 10);
        ShellModel.Rebind([ambiguous], [Reg("s1", SessionStatus.Idle, pid: 20), Reg("s1", SessionStatus.Idle, pid: 21)], () => tree);
        Assert.Equal((10, 10, 10), (unrelated.Pid, nested.Pid, ambiguous.Pid));

        var registered = Live("s1", pid: 20); // its own entry is there: the process list is not even read
        ShellModel.Rebind([registered, Live("s2", SessionStatus.Exited)], [Reg("s1", SessionStatus.Idle, pid: 20)], () => throw new InvalidOperationException());
        Assert.Equal(20, registered.Pid);
    }

    [Fact]
    public void IsDescendant_follows_a_few_parent_links_and_stops_on_loops()
    {
        var tree = new Dictionary<int, int> { [3] = 2, [2] = 1, [7] = 8, [8] = 7 };
        Assert.True(ProcessTree.IsDescendant(tree, 3, 1));
        Assert.False(ProcessTree.IsDescendant(tree, 1, 3));
        Assert.False(ProcessTree.IsDescendant(tree, 7, 1)); // pid reuse made a loop
        Assert.True(ProcessTree.IsDescendant(ProcessTree.Parents(), Environment.ProcessId, ProcessTree.Parents()[Environment.ProcessId]));
    }

    [Fact]
    public void A_cleared_session_keeps_its_row_instead_of_showing_running_elsewhere()
    {
        var tab = Live("old", SessionStatus.Idle, pid: 10); // the poll has not adopted the new id yet
        var catalog = new[] { Entry("new", "After clear", @"C:\p\open", TimeSpan.FromSeconds(5), Reg("new", SessionStatus.Busy, pid: 10)) };

        var row = Assert.Single(SessionRow.Build(catalog, [tab], tab, Now)); // no second row for the unknown old id
        Assert.Equal(("new", true, false, true), (row.Key, row.IsOpen, row.IsElsewhere, row.IsActive));

        var rows = SessionRow.Build(catalog, [Live("old", pid: 11)], null, Now); // another pid: the process is really elsewhere
        Assert.Equal(("new", true), (rows[1].Key, rows[1].IsElsewhere));
    }

    [Fact]
    public void Rows_merge_open_sessions_with_the_catalog_newest_first()
    {
        var catalog = new[]
        {
            Entry("old", "Old chat", @"C:\p\old", TimeSpan.FromDays(3)),
            Entry("open", "Open chat", @"C:\p\open", TimeSpan.FromMinutes(5)),
            Entry("away", "Elsewhere", @"C:\p\away", TimeSpan.FromHours(1), Reg("away", SessionStatus.Busy)),
        };
        var open = Live("open", SessionStatus.Waiting);
        var fresh = Live("fresh"); // no transcript yet
        var rows = SessionRow.Build(catalog, [open, fresh], open, Now);

        Assert.Equal(["fresh", "open", "away", "old"], rows.Select(r => r.Key));
        Assert.Equal(("◉", true, true, "Open chat", "open"), (rows[1].Dot, rows[1].IsOpen, rows[1].IsActive, rows[1].Title, rows[1].Project));
        Assert.Equal(("○", "New session"), (rows[0].Dot, rows[0].Title));
        Assert.Equal(("●", false, true), (rows[2].Dot, rows[2].IsOpen, rows[2].IsElsewhere)); // running in another window
        Assert.Equal(("", false, false, "3d"), (rows[3].Dot, rows[3].IsOpen, rows[3].IsElsewhere, rows[3].Age));
    }

    [Fact]
    public void An_exited_session_shows_no_dot_unless_a_process_still_runs()
    {
        var ended = Live("s1", SessionStatus.Exited);
        var catalog = new[] { Entry("s1", "Ended", @"C:\p\open", TimeSpan.FromHours(1)) };
        Assert.Equal("", Assert.Single(SessionRow.Build(catalog, [ended], null, Now)).Dot);
        Assert.Equal("", Assert.Single(SessionRow.Build([], [ended], null, Now)).Dot);

        var running = new[] { Entry("s1", "Ended", @"C:\p\open", TimeSpan.FromHours(1), Reg("s1", SessionStatus.Idle)) };
        Assert.Equal("○", Assert.Single(SessionRow.Build(running, [ended], null, Now)).Dot);
    }

    [Fact]
    public void Rows_compare_equal_across_refreshes_unless_something_shown_changes()
    {
        static SessionRow Row(SessionStatus status, long updated, TimeSpan ago) => Assert.Single(SessionRow.Build(
            [Entry("away", "Elsewhere", @"C:\p\away", ago, Reg("away", status, updated: updated))], [], null, Now));

        var before = Row(SessionStatus.Busy, 1, TimeSpan.FromSeconds(5));
        Assert.Equal(before, Row(SessionStatus.Busy, 4, TimeSpan.FromSeconds(2))); // only Running.UpdatedAt and LastActivity moved
        Assert.True(before == Row(SessionStatus.Busy, 4, TimeSpan.FromSeconds(2)));
        Assert.Equal(before.GetHashCode(), Row(SessionStatus.Busy, 4, TimeSpan.Zero).GetHashCode());
        Assert.NotEqual(before, Row(SessionStatus.Idle, 4, TimeSpan.FromSeconds(2))); // the dot changed
        Assert.NotEqual(before, Row(SessionStatus.Busy, 4, TimeSpan.FromMinutes(5))); // the age text changed
    }

    [Theory]
    [InlineData(@"C:\proj\", @"C:\proj")]
    [InlineData(@"C:\proj", @"C:\proj")]
    [InlineData(@"  ""C:/proj/sub/""  ", @"C:\proj\sub")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("", "")]
    [InlineData("a\0b", "a\0b")] // unusable text is passed on; Directory.Exists then says no
    public void Typed_folders_lose_their_trailing_separator(string typed, string folder) => Assert.Equal(folder, ShellModel.NormalizeFolder(typed));

    [Fact]
    public void Rows_filter_on_title_and_project_or_restrict_to_search_hits()
    {
        var catalog = new[] { Entry("s1", "Fix login", @"C:\p\Alpha", TimeSpan.FromHours(1)), Entry("s2", "Docs", @"C:\p\Beta", TimeSpan.FromHours(2)) };

        Assert.Equal(["s1"], SessionRow.Build(catalog, [], null, Now, "LOGIN").Select(r => r.Key));
        Assert.Equal(["s2"], SessionRow.Build(catalog, [], null, Now, "beta").Select(r => r.Key));

        var hits = new Dictionary<string, SearchHit> { ["s2"] = new("s2", "p", 2, "…the needle…") };
        var found = Assert.Single(SessionRow.Build(catalog, [Live("fresh")], null, Now, "login", hits));
        Assert.Equal(("s2", "…the needle…"), (found.Key, found.Snippet));
    }

    [Fact]
    public void Env_drops_the_variables_a_parent_claude_session_sets()
    {
        var parent = new Hashtable
        {
            ["Path"] = @"C:\bin", ["claudecode"] = "1", ["CLAUDE_CODE_SESSION_ID"] = "x", ["CLAUDE_CODE_ENTRYPOINT"] = "cli",
            ["CLAUDE_EFFORT"] = "high", ["CLAUDE_CODE_SSE_PORT"] = "1234", ["CLAUDE_CODE_USE_BEDROCK"] = "0",
        };
        var env = ClaudeLaunch.Env(C, Guid.NewGuid(), parent);

        Assert.Equal(new[] { "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CONFIG_DIR", "CCW_TAB", "COLORTERM", "Path" }.Order(StringComparer.OrdinalIgnoreCase),
            env.Keys.Order(StringComparer.OrdinalIgnoreCase));
    }
}
