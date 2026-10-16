using System.IO;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;
using CCWrapper.Views;

namespace CCWrapper.Tests.Input;

public sealed class InputTests
{
    sealed class FakeChannel(string? screen = null, Action? onText = null) : IScreenChannel
    {
        public List<string> Sent { get; } = [];
        public void SendText(string text) { Sent.Add("text:" + text); onText?.Invoke(); }
        public void SendKeys(string keys) => Sent.Add("keys:" + keys);
        // Like ccw.tail: only the last <rows> non-blank rows.
        public Task<string?> ReadTailAsync(int rows) =>
            Task.FromResult(screen is null ? null : string.Join('\n', screen.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(rows)));
    }

    static LiveSession Session(SessionStatus status, string? waitingFor = null, bool channel = true, Account? account = null) =>
        new(account ?? new Account("c", @"C:\x", @"C:\x\.claude.json", "t"))
        {
            Status = status, WaitingFor = waitingFor, Channel = channel ? new FakeChannel() : null,
        };

    static List<string> Sent(LiveSession s) => ((FakeChannel)s.Channel!).Sent;

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
        Assert.Null(await TuiInput.SubmitAsync(s, "/help"));
        Assert.Equal(["text:/help", "keys:\r"], Sent(s));
    }

    [Fact]
    public async Task Submit_sends_nothing_when_blocked()
    {
        var s = Session(SessionStatus.Waiting, "dialog open");
        Assert.NotNull(await TuiInput.SubmitAsync(s, "/help"));
        Assert.Empty(Sent(s));
        Assert.Equal("No active session", await TuiInput.SubmitAsync(null, "/help"));
    }

    [Fact]
    public async Task Switching_mode_blocks_input_until_it_ends()
    {
        var s = Session(SessionStatus.Idle);
        TuiInput.SetSwitching(s, true);
        Assert.Equal("Switching mode…", TuiInput.BlockedReason(s));
        Assert.NotNull(await TuiInput.SubmitAsync(s, "hello"));
        Assert.Empty(Sent(s));
        TuiInput.SetSwitching(s, false);
        Assert.Null(TuiInput.BlockedReason(s));
    }

    /// <summary>A temp account whose registry, once written, says this test process is a claude.exe on a permission prompt.</summary>
    sealed class WaitingRegistry : IDisposable
    {
        readonly string _root = Directory.CreateTempSubdirectory("ccw-registry-").FullName;

        public WaitingRegistry() => Directory.CreateDirectory(Account.Paths.Sessions);

        public Account Account => new("c", _root, Path.Combine(_root, ".claude.json"), "t");

        public void Write() => File.WriteAllText(Path.Combine(Account.Paths.Sessions, $"{Environment.ProcessId}.json"),
            $$"""{ "pid": {{Environment.ProcessId}}, "sessionId": "s1", "status": "waiting", "waitingFor": "permission prompt" }""");

        public void WriteRaw(string json) =>
            File.WriteAllText(Path.Combine(Account.Paths.Sessions, $"{Environment.ProcessId}.json"), json);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(null)]                    // no entry at all
    [InlineData("{ \"pid\": 123, \"sessi")] // torn mid-write
    public async Task Submit_is_blocked_when_the_tabs_own_registry_entry_cannot_be_read(string? entry)
    {
        using var registry = new WaitingRegistry();
        if (entry is not null) registry.WriteRaw(entry);
        var s = Session(SessionStatus.Idle, account: registry.Account);
        s.Pid = Environment.ProcessId;
        Assert.Contains("Could not read", await TuiInput.SubmitAsync(s, "hello"));
        Assert.Empty(Sent(s));
    }

    [Fact]
    public async Task Fresh_read_passes_when_the_tabs_entry_says_idle()
    {
        using var registry = new WaitingRegistry();
        registry.WriteRaw($$"""{ "pid": {{Environment.ProcessId}}, "sessionId": "s1", "status": "idle" }""");
        var s = Session(SessionStatus.Idle, account: registry.Account);
        s.Pid = Environment.ProcessId;
        Assert.Null(await TuiInput.FreshBlockedReasonAsync(s));
    }

    [Fact]
    public async Task Fresh_read_follows_a_launcher_to_the_claude_process_it_started()
    {
        using var registry = new WaitingRegistry();
        registry.WriteRaw($$"""{ "pid": {{Environment.ProcessId}}, "sessionId": "s1", "status": "idle" }""");
        var s = Session(SessionStatus.Idle, account: registry.Account);
        s.SessionId = "s1";
        s.Pid = ProcessTree.Parents()[Environment.ProcessId]; // the tab spawned a shim; this process plays the claude.exe it started
        Assert.Null(await TuiInput.FreshBlockedReasonAsync(s));
        Assert.Equal(Environment.ProcessId, s.Pid);
    }

    [Fact]
    public async Task Submit_reads_the_registry_fresh_before_the_paste()
    {
        using var registry = new WaitingRegistry();
        registry.Write();
        var s = Session(SessionStatus.Idle, account: registry.Account); // the last poll still said idle
        s.Pid = Environment.ProcessId;
        Assert.Contains("permission prompt", await TuiInput.SubmitAsync(s, "1"));
        Assert.Empty(Sent(s));
    }

    [Fact]
    public async Task Submit_reads_the_registry_fresh_before_the_enter()
    {
        using var registry = new WaitingRegistry();
        var s = Session(SessionStatus.Idle, channel: false, account: registry.Account);
        s.Channel = new FakeChannel(onText: registry.Write); // a prompt opens right after the paste
        s.SessionId = "s1"; // no pid: matched by session id
        Assert.Contains("not sent", await TuiInput.SubmitAsync(s, "hello"));
        Assert.Equal(["text:hello"], Sent(s)); // no Enter
    }

    [Theory]
    [InlineData(0, 80)]
    [InlineData(99, 80)]
    [InlineData(100, 81)]
    [InlineData(10_000, 180)]
    [InlineData(92_000, 1000)]
    [InlineData(5_000_000, 1000)]
    public void Enter_delay_grows_with_the_paste(int length, int ms) =>
        Assert.Equal(TimeSpan.FromMilliseconds(ms), TuiInput.EnterDelay(length));

    const string Rule = "────────────────────────────────";

    [Theory]
    [InlineData("> draft", true)]
    [InlineData("❯ fix the bug", true)]
    [InlineData("❯\u00a0/model", true)]
    [InlineData("│ > half typed               │", true)]
    [InlineData("> ", false)]
    [InlineData("❯", false)]
    [InlineData("│ >                          │", false)]
    [InlineData("> Try \"refactor ui.py\"", false)] // placeholder
    [InlineData("! ls -la", true)]                  // bash mode
    [InlineData("!", false)]
    public void Prompt_row_text_is_found_under_the_rule(string row, bool typed) =>
        Assert.Equal(typed, TuiInput.HasPromptText($"> old prompt\n  ⎿ answer\n{Rule}\n{row}\n{Rule}\n  ? for shortcuts\n> statusline\n\n\n"));

    [Fact]
    public void Prompt_row_needs_a_rule_above_it()
    {
        Assert.False(TuiInput.HasPromptText("> echoed prompt\n  ⎿ answer"));
        Assert.False(TuiInput.HasPromptText(null));
        Assert.True(TuiInput.HasPromptText("╭──────────╮\n│ > typed  │\n╰──────────╯"));
    }

    [Fact]
    public void Prompt_row_needs_a_rule_below_it_too()
    {
        Assert.False(TuiInput.HasPromptText($"{Rule}\n> typed"));                         // cut off: no bottom rule
        Assert.False(TuiInput.HasPromptText($"{Rule}\n> \n{Rule}\n❯ main · 3 files"));     // a status line under the box is not the input
        Assert.True(TuiInput.HasPromptText($"{Rule}\n> typed\n{Rule}\n❯ main · 3 files")); // ... and does not hide the input
        Assert.True(TuiInput.HasPromptText($"{Rule}\n> \n  second line\n{Rule}"));         // multi-line input
    }

    [Theory]
    [InlineData("!", true)]
    [InlineData("! ls -la", true)]
    [InlineData("│ ! ls        │", true)]
    [InlineData("> ! not bash", false)]
    [InlineData("> ls", false)]
    public void Bash_mode_is_the_prompt_rows_marker(string row, bool bash)
    {
        Assert.Equal(bash, TuiInput.InBashMode($"{Rule}\n{row}\n{Rule}\n  ? for shortcuts"));
        Assert.False(TuiInput.InBashMode($"! echoed\n  ⎿ out\n{Rule}\n> \n{Rule}")); // a "!" row in the transcript
    }

    [Fact]
    public async Task Submit_is_refused_above_a_tall_multi_line_bash_input()
    {
        var s = Session(SessionStatus.Idle, channel: false);
        s.Channel = new FakeChannel($"{Rule}\n! for f in *; do\n{string.Concat(Enumerable.Repeat("    echo line\n", 15))}  done\n{Rule}\n  ? for shortcuts");
        Assert.Equal(TuiInput.BashModeFirst, await TuiInput.SubmitAsync(s, "hello"));
        Assert.Empty(Sent(s));
    }

    [Theory]
    [InlineData("!")]
    [InlineData("! ls")]
    public async Task Any_submit_is_refused_while_the_terminal_input_is_in_bash_mode(string row)
    {
        var s = Session(SessionStatus.Idle, channel: false);
        s.Channel = new FakeChannel($"{Rule}\n{row}\n{Rule}");
        Assert.Equal(TuiInput.BashModeFirst, await TuiInput.SubmitAsync(s, "hello"));
        Assert.Equal(TuiInput.BashModeFirst, await TuiInput.SubmitAsync(s, "/model opus"));
        Assert.Empty(Sent(s));
    }

    [Fact]
    public async Task Slash_command_is_refused_while_the_terminal_input_has_text()
    {
        var s = Session(SessionStatus.Idle, channel: false);
        s.Channel = new FakeChannel($"{Rule}\n> half typed\n{Rule}");
        Assert.Equal(TuiInput.ClearInputFirst, await TuiInput.SubmitAsync(s, "/model opus"));
        Assert.Empty(Sent(s));
        Assert.Null(await TuiInput.SubmitAsync(s, "more text")); // a prompt is still added to it
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
    [InlineData("  ? for shortcuts", "default")]
    [InlineData("  esc to interrupt", null)]
    public void Mode_is_read_from_the_footer_rows(string footer, string? mode) =>
        Assert.Equal(mode, BarFormat.ModeFromScreen("Claude Code\n\n❯ \n──────\n" + footer + "\n"));

    [Fact]
    public void Mode_is_found_above_trailing_blank_rows()
    {
        var blanks = string.Concat(Enumerable.Repeat("        \n", 30)); // a screen that is not full yet
        Assert.Equal("acceptEdits", BarFormat.ModeFromScreen("❯ \n──────\n  ⏵⏵ accept edits on (shift+tab to cycle)\n" + blanks));
        Assert.Equal("default", BarFormat.ModeFromScreen("❯ \n──────\n  ? for shortcuts\n" + blanks));
    }

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
    public void Mode_cycle_presses_only_on_a_fresh_footer_toward_an_unseen_mode()
    {
        Assert.True(ModeCycle.ShouldPress(null, "default", [], "plan"));
        Assert.True(ModeCycle.ShouldPress(null, "acceptEdits", ["default"], "plan"));
        Assert.False(ModeCycle.ShouldPress("Claude is waiting (permission prompt)", "default", [], "plan")); // Shift+Tab would answer it
        Assert.False(ModeCycle.ShouldPress(null, null, [], "plan"));                                         // no footer on screen now
        Assert.False(ModeCycle.ShouldPress(null, "plan", ["default", "acceptEdits"], "plan"));               // arrived
        Assert.False(ModeCycle.ShouldPress(null, "default", ["default", "acceptEdits", "plan"], "auto"));    // back at the start
        Assert.False(ModeCycle.ShouldPress(null, "acceptEdits", ["default", "acceptEdits"], "plan"));        // the press changed nothing
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
    public void Ctx_prefers_the_status_line_then_transcript_tokens()
    {
        var sl = new StatusLineData { ContextWindowSize = 1_000_000, ContextUsedPercent = 20 };
        Assert.Equal("ctx 200k/1M 20%", BarFormat.Ctx(sl, 650_000));          // window x percent, also right after a /compact
        Assert.Equal("ctx 200k/1M 20%", BarFormat.Ctx(sl, null));
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
    public void Usage_uses_the_newer_of_the_status_line_and_the_saved_value_with_its_time()
    {
        var savedAt = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var cached = new AccountInfo(null, null, null, null, null, savedAt,
            new UsageWindow(81, null), new UsageWindow(12.4, null), null, null, null);

        var live = BarFormat.Usage(new() { FiveHourUsedPercent = 58, SevenDayUsedPercent = 9 }, cached); // read now: newer
        Assert.Equal("5h 58% · wk 9%", live.Text);
        Assert.Contains("status line", live.Tip);

        var older = BarFormat.Usage(new() { FiveHourUsedPercent = 58, ReceivedAt = savedAt.AddMinutes(-5) }, cached); // a /usage ran since
        Assert.Equal("5h 81% · wk 12%", older.Text);

        var fallback = BarFormat.Usage(new(), cached);
        Assert.Equal("5h 81% · wk 12%", fallback.Text);
        Assert.Contains("as of 2026-10-09", fallback.Tip);

        Assert.Equal("5h — · wk —", BarFormat.Usage(null, null).Text);
    }

    [Fact]
    public void Newer_picks_the_account_read_with_the_later_saved_usage()
    {
        AccountInfo At(int day) => new(null, null, null, null, null, new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.Zero), null, null, null, null, null);
        var (old, fresh) = (At(9), At(16));
        Assert.Same(fresh, BarFormat.Newer(old, fresh));
        Assert.Same(fresh, BarFormat.Newer(fresh, old));
        Assert.Same(old, BarFormat.Newer(old, null));
        Assert.Same(old, BarFormat.Newer(null, old));
        Assert.Null(BarFormat.Newer(null, null));
    }
}
