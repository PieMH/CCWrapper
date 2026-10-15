using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Views;

namespace CCWrapper.Tests.Input;

public sealed class InputTests
{
    sealed class FakeChannel : ITerminalChannel
    {
        public List<string> Sent { get; } = [];
        public void SendText(string text) => Sent.Add("text:" + text);
        public void SendKeys(string keys) => Sent.Add("keys:" + keys);
    }

    static LiveSession Session(SessionStatus status, string? waitingFor = null, bool channel = true) =>
        new(new Account("c", @"C:\x", @"C:\x\.claude.json", "t"))
        {
            Status = status, WaitingFor = waitingFor, Channel = channel ? new FakeChannel() : null,
        };

    [Fact]
    public void Blocked_when_no_session_exited_or_a_dialog_is_open()
    {
        Assert.Equal("No active session", TuiInput.BlockedReason(null));
        Assert.NotNull(TuiInput.BlockedReason(Session(SessionStatus.Exited)));
        Assert.Contains("permission prompt", TuiInput.BlockedReason(Session(SessionStatus.Waiting, "permission prompt")));
        Assert.Contains("terminal", TuiInput.BlockedReason(Session(SessionStatus.Waiting, "some future value")));
        Assert.Contains("dialog open", TuiInput.BlockedReason(Session(SessionStatus.Waiting)));
        Assert.NotNull(TuiInput.BlockedReason(Session(SessionStatus.Idle, channel: false)));
    }

    [Theory]
    [InlineData(SessionStatus.Unknown)]
    [InlineData(SessionStatus.Idle)]
    [InlineData(SessionStatus.Busy)]
    public void Open_while_running_even_with_a_stale_waitingFor(SessionStatus status) =>
        Assert.Null(TuiInput.BlockedReason(Session(status, waitingFor: "dialog open")));

    [Fact]
    public async Task Submit_pastes_then_presses_enter()
    {
        var s = Session(SessionStatus.Idle);
        Assert.True(await TuiInput.SubmitAsync(s, "/help"));
        Assert.Equal(["text:/help", "keys:\r"], ((FakeChannel)s.Channel!).Sent);
    }

    [Fact]
    public async Task Submit_sends_nothing_when_blocked()
    {
        var s = Session(SessionStatus.Waiting, "dialog open");
        Assert.False(await TuiInput.SubmitAsync(s, "/help"));
        Assert.Empty(((FakeChannel)s.Channel!).Sent);
    }

    [Fact]
    public void History_browses_back_and_forward()
    {
        var h = new PromptHistory();
        Assert.Null(h.Previous(""));
        h.Add("one");
        h.Add("two");
        Assert.Null(h.Previous("typed")); // a draft is not replaced
        Assert.Equal("two", h.Previous(""));
        Assert.Equal("one", h.Previous("two"));
        Assert.Null(h.Previous("one"));    // oldest
        Assert.Equal("two", h.Next("one"));
        Assert.Equal("", h.Next("two"));   // past the newest: empty box
        Assert.Null(h.Next(""));
        Assert.Equal("two", h.Previous("")); // empty box starts from the newest again
    }

    [Fact]
    public void History_does_not_repeat_the_last_prompt()
    {
        var h = new PromptHistory();
        h.Add("a");
        h.Add("a");
        Assert.Equal("a", h.Previous(""));
        Assert.Null(h.Previous("a"));
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0L, "0")]
    [InlineData(999L, "999")]
    [InlineData(1200L, "1.2k")]
    [InlineData(10_000L, "10k")]
    [InlineData(48_300L, "48k")]
    [InlineData(202_000L, "202k")]
    [InlineData(999_600L, "1M")]
    [InlineData(1_000_000L, "1M")]
    [InlineData(1_240_000L, "1.2M")]
    public void Tokens_use_k_and_m(long? n, string expected) => Assert.Equal(expected, BarFormat.Tokens(n));

    [Theory]
    [InlineData("  ⏵⏵ auto mode on (shift+tab to cycle) · ← 1 agent", "auto")]
    [InlineData("  ⏸ plan mode on (shift+tab to cycle)", "plan")]
    [InlineData("  ⏵⏵ accept edits on (shift+tab to cycle)", "acceptEdits")]
    [InlineData("  ⏵⏵ bypass permissions on (shift+tab to cycle)", "bypassPermissions")]
    [InlineData("  ‖ manual mode on · ← 1 agent", "default")]
    [InlineData("  ? for shortcuts", null)]
    public void Mode_is_read_from_the_footer_rows(string footer, string? mode) =>
        Assert.Equal(mode, BarFormat.ModeFromScreen("Claude Code\n\n❯ \n──────\n" + footer + "\n"));

    [Fact]
    public void Mode_ignores_older_lines_and_missing_screens()
    {
        var scrolledAway = "  ⏸ plan mode on\n" + string.Concat(Enumerable.Repeat("some output\n", 12));
        Assert.Null(BarFormat.ModeFromScreen(scrolledAway));
        Assert.Equal("auto", BarFormat.ModeFromScreen("  ⏸ plan mode on\n" + string.Concat(Enumerable.Repeat("x\n", 5)) + "  ⏵⏵ auto mode on"));
        Assert.Null(BarFormat.ModeFromScreen(null));
        Assert.Null(BarFormat.ModeFromScreen("the plan mode on the roadmap")); // must start the line
    }

    [Fact]
    public void Cost_and_percent_format()
    {
        Assert.Equal("$0.42", BarFormat.Cost(0.4213));
        Assert.Equal("$16.7", BarFormat.Cost(16.74));
        Assert.Equal("—", BarFormat.Cost(null));
        Assert.Equal("58%", BarFormat.Percent(58.2));
        Assert.Equal("—", BarFormat.Percent(null));
    }

    [Fact]
    public void Ctx_prefers_status_line_percent_and_transcript_tokens()
    {
        var sl = new StatusLineData { ContextWindowSize = 1_000_000, ContextUsedPercent = 20 };
        Assert.Equal("ctx 202k/1M 20%", BarFormat.Ctx(sl, 202_000));
        Assert.Equal("ctx 200k/1M 20%", BarFormat.Ctx(sl, null));             // tokens derived from window x percent
        Assert.Equal("ctx 100k/1M 10%", BarFormat.Ctx(new() { ContextWindowSize = 1_000_000 }, 100_000)); // percent derived
        Assert.Equal("ctx 50k/— —", BarFormat.Ctx(null, 50_000));
        Assert.Equal("ctx —", BarFormat.Ctx(null, null));
    }

    [Fact]
    public void Spend_falls_back_to_the_transcript()
    {
        var sl = new StatusLineData { TotalInputTokens = 1_200_000, TotalOutputTokens = 48_000, CostUsd = 16.74 };
        Assert.Equal("in 1.2M out 48k $16.7", BarFormat.Spend(sl, 1, 1));
        Assert.Equal("in — out 7.5k $1.50", BarFormat.Spend(null, 7_500, 1.5));
        Assert.Equal("in — out — —", BarFormat.Spend(null, null, null));
    }

    [Fact]
    public void Usage_uses_the_status_line_else_the_cached_value_with_its_age()
    {
        var cached = new AccountInfo(null, null, null, null, null, new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
            new UsageWindow(81, null), new UsageWindow(12.4, null), null, null, null);

        var live = BarFormat.Usage(new() { FiveHourUsedPercent = 58, SevenDayUsedPercent = 9 }, cached);
        Assert.Equal("5h 58% · wk 9%", live.Text);
        Assert.DoesNotContain("as of", live.Tip);

        var fallback = BarFormat.Usage(new(), cached);
        Assert.Equal("5h 81% · wk 12%", fallback.Text);
        Assert.Contains("as of 2026-10-09", fallback.Tip);

        Assert.Equal("5h — · wk —", BarFormat.Usage(null, null).Text);
    }
}
