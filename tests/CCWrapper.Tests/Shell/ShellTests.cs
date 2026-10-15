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

    static RunningSession Reg(string id, SessionStatus status, string? waitingFor = null, long updated = 0) =>
        new(1, id, @"C:\p", null, status, waitingFor, "interactive", DateTimeOffset.FromUnixTimeSeconds(updated));

    static LiveSession Live(string id, SessionStatus status = SessionStatus.Idle) =>
        new(C) { SessionId = id, Cwd = @"C:\p\open", Status = status };

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
    public void MapStatus_matches_open_sessions_by_id_and_skips_exited()
    {
        var busy = Live("s1");
        var exited = Live("s2", SessionStatus.Exited);
        var unregistered = Live("s3");
        var result = ShellModel.MapStatus([busy, exited, unregistered],
            [Reg("s1", SessionStatus.Waiting, "permission", updated: 1), Reg("s1", SessionStatus.Busy, updated: 5), Reg("s2", SessionStatus.Busy)]).ToList();

        var (session, status, waiting) = Assert.Single(result); // the newest registry entry of s1 wins
        Assert.Equal((busy, SessionStatus.Busy, (string?)null), (session, status, waiting));
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
