using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;

namespace CCWrapper.Views;

public partial class BottomBar : UserControl
{
    static readonly string[] Models = ["opus", "sonnet", "haiku", "fable"];
    static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    static readonly string[] Modes = ["default", "acceptEdits", "plan", "auto"];
    const string ShiftTab = "\x1b[Z";
    const string SavedAsDefault = "Claude Code saves this as the account's default";

    BarData? _data;
    LiveSession? _session;
    (LiveSession Session, string? Mode) _screenMode;
    string? _model, _effort, _mode;

    public BottomBar()
    {
        InitializeComponent();
        _ = new SessionFollower<BarSource, BarData>(this, s => new BarSource(s), r => r.Read(), async d =>
        {
            _data = d;
            if (_session is { } s) await ReadScreenModeAsync(s);
            Render();
        });
        _ = new SessionWatch(this, s => { _session = s; Render(); });
    }

    void Render()
    {
        var s = _session;
        var sl = s?.StatusLine;
        var d = _data is { } x && x.Session == s ? x : null; // a poll result may still belong to the previous session
        // statusLine first, then the transcript, then the account's settings defaults
        _model = sl?.ModelName ?? d?.Model ?? d?.Account?.Model;
        _effort = sl?.Effort ?? d?.Effort ?? d?.Account?.Effort;
        // The transcript's permission-mode record is written per turn, not when Shift+Tab changes the mode: the footer is the truth.
        _mode = (_screenMode.Session == s ? _screenMode.Mode : null) ?? d?.Mode;
        ModelButton.Content = $"{_model ?? BarFormat.None} ▾";
        EffortButton.Content = $"effort {_effort ?? BarFormat.None} ▾";
        ModeButton.Content = $"mode {_mode ?? BarFormat.None} ▾";

        var reason = TuiInput.BlockedReason(s);
        foreach (var b in new[] { ModelButton, EffortButton, ModeButton }) b.IsEnabled = reason is null;
        ModelButton.ToolTip = EffortButton.ToolTip = reason ?? SavedAsDefault;
        ModeButton.ToolTip = reason;
        CtxText.Text = BarFormat.Ctx(sl, d?.ContextTokens);
        SpendText.Text = BarFormat.Spend(sl, d?.OutputTokens, d?.CostUsd);
        (UsageText.Text, UsageText.ToolTip) = BarFormat.Usage(sl, d?.Account);
    }

    // A refusal (e.g. text already in the terminal input) shows in the compose box's notice.
    void OnModel(object sender, RoutedEventArgs e) =>
        Open(ModelButton, Models, v => _model?.Contains(v, StringComparison.OrdinalIgnoreCase) == true, v => TuiInput.SubmitAsync(_session, "/model " + v));

    void OnEffort(object sender, RoutedEventArgs e) => Open(EffortButton, Efforts, v => v == _effort, v => TuiInput.SubmitAsync(_session, "/effort " + v));

    void OnMode(object sender, RoutedEventArgs e) => Open(ModeButton, Modes, v => v == _mode, SetModeAsync);

    static void Open(Button button, string[] values, Func<string, bool> isCurrent, Func<string, Task> pick)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Top };
        foreach (var v in values)
        {
            var item = new MenuItem { Header = v, IsCheckable = true, IsChecked = isCurrent(v) };
            item.Click += async (_, _) => await pick(v);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>The mode in the terminal's footer (see <see cref="BarFormat.ModeFromScreen"/>), also kept for display; null if
    /// there is no terminal or no footer.</summary>
    async Task<string?> ReadScreenModeAsync(LiveSession s)
    {
        var tail = s.Channel is IScreenChannel screen ? await screen.ReadTailAsync(BarFormat.FooterRows) : null;
        _screenMode = (s, BarFormat.ModeFromScreen(tail));
        return _screenMode.Mode;
    }

    /// <summary>Shift+Tab until the footer shows <paramref name="target"/>. Before each press <see cref="ModeCycle.ShouldPress"/>
    /// decides on the registry read fresh and the footer read just now; after it, waits up to ~3 s for the footer to change.</summary>
    async Task SetModeAsync(string target)
    {
        var s = _session;
        if (s is null || TuiInput.BlockedReason(s) is not null) return;
        TuiInput.SetSwitching(s, true);
        Render();
        try
        {
            var seen = new List<string>();
            var mode = await ReadScreenModeAsync(s);
            while (ModeCycle.ShouldPress(await TuiInput.FreshBlockedReasonAsync(s, switching: true), mode, seen, target) && s.SendKeys(ShiftTab))
            {
                seen.Add(mode!);
                for (var wait = 0; wait < 20; wait++)
                {
                    await Task.Delay(150);
                    if ((mode = await ReadScreenModeAsync(s)) is not null && mode != seen[^1]) break;
                }
            }
        }
        finally
        {
            TuiInput.SetSwitching(s, false);
            Render();
        }
    }
}

/// <summary>The Shift+Tab loop's rule. In a permission dialog Shift+Tab answers "allow all edits", so it only presses on evidence.</summary>
public static class ModeCycle
{
    /// <summary>Whether one more Shift+Tab goes toward <paramref name="target"/>: nothing blocks input (<paramref name="blocked"/>
    /// read fresh), the footer showed <paramref name="mode"/> just now (never the transcript's), it is not the target yet, and it
    /// is not in <paramref name="seen"/> (the modes before each earlier press): seeing one again means a full cycle without the
    /// target, or a press that changed nothing.</summary>
    public static bool ShouldPress(string? blocked, string? mode, IReadOnlyCollection<string> seen, string target) =>
        blocked is null && mode is not null && mode != target && !seen.Contains(mode);
}

/// <summary>What one poll of the active session found. Null fields are unknown.</summary>
internal sealed record BarData(LiveSession Session, string? Model, string? Effort, string? Mode,
    long? ContextTokens, long? OutputTokens, double? CostUsd, AccountInfo? Account);

/// <summary>Owns the session's transcript tail. Read is serialized: the 2 s follower and a mode switch both call it.</summary>
internal sealed class BarSource(LiveSession session)
{
    static readonly TimeSpan AccountMaxAge = TimeSpan.FromSeconds(30);

    readonly TranscriptReader _transcript = new(session.TranscriptPath!);
    readonly object _gate = new();
    AccountInfo? _account;
    DateTime _accountAt;

    public LiveSession Session { get; } = session;

    /// <summary>Does file IO: off the UI thread.</summary>
    public BarData Read()
    {
        lock (_gate)
        {
            _transcript.Poll();
            if (_account is null || DateTime.UtcNow - _accountAt > AccountMaxAge)
            {
                _account = AccountReader.Read(Session.Account); // settings defaults and the cached usage
                _accountAt = DateTime.UtcNow;
            }
            var t = _transcript;
            return new(Session, t.Model, t.Effort, t.PermissionMode, t.ContextTokens, t.OutputTokens > 0 ? t.OutputTokens : null, t.CostUsd, _account);
        }
    }
}

/// <summary>Meter texts. "—" marks what is not known.</summary>
public static class BarFormat
{
    public const string None = "—";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>999, 1.2k, 48k, 202k, 1M, 1.2M.</summary>
    public static string Tokens(long? n) => n switch
    {
        null => None,
        < 1000 => n.Value.ToString(Inv),
        < 999_500 => Scaled(n.Value / 1e3, "k"),
        _ => Scaled(n.Value / 1e6, "M"),
    };

    static string Scaled(double v, string unit) => v.ToString(v < 10 ? "0.#" : "0", Inv) + unit;

    public static string Percent(double? p) => p is { } v ? v.ToString("0", Inv) + "%" : None;

    public static string Cost(double? usd) => usd is { } v ? "$" + v.ToString(v < 10 ? "0.00" : "0.0", Inv) : None;

    /// <summary>"ctx 200k/1M 20%". Used tokens are window x percent when the status line has both (it stays right after a
    /// /compact), else the transcript's last turn; the percent is the status line's, else used / window.</summary>
    public static string Ctx(StatusLineData? sl, long? lastTurnTokens)
    {
        var size = sl?.ContextWindowSize;
        var pct = sl?.ContextUsedPercent;
        var used = size is { } w && pct is { } p ? (long)(w * p / 100) : lastTurnTokens;
        pct ??= used is { } u && size is > 0 ? u * 100.0 / size : null;
        return used is null && size is null && pct is null ? "ctx " + None : $"ctx {Tokens(used)}/{Tokens(size)} {Percent(pct)}";
    }

    static readonly Dictionary<string, string> FooterModes = new()
    {
        ["manual mode"] = "default", ["plan mode"] = "plan", ["accept edits"] = "acceptEdits",
        ["bypass permissions"] = "bypassPermissions", ["don't ask"] = "dontAsk", ["auto mode"] = "auto",
    };

    /// <summary>How many of the screen's last non-blank rows <see cref="ModeFromScreen"/> looks at.</summary>
    public const int FooterRows = 10;

    /// <summary>The permission mode named by the TUI's footer ("⏵⏵ auto mode on (shift+tab to cycle)") among the last non-blank
    /// rows of the terminal text, or null. These are Claude Code's own indicator names; the default mode shows none, only
    /// "? for shortcuts".</summary>
    public static string? ModeFromScreen(string? screen)
    {
        if (screen is null) return null;
        var rows = screen.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(FooterRows).Reverse().ToList();
        foreach (var line in rows)
            if (Regex.Match(line, @"^\W*(manual mode|plan mode|accept edits|bypass permissions|don't ask|auto mode) on\b") is { Success: true } m)
                return FooterModes[m.Groups[1].Value];
        return rows.Any(l => Regex.IsMatch(l, @"^\s*\? for shortcuts")) ? "default" : null;
    }

    /// <summary>"in 1.2M out 48k $16.7": the status line's session totals, else the transcript's output and cost.</summary>
    public static string Spend(StatusLineData? sl, long? transcriptOutput, double? transcriptCost) =>
        $"in {Tokens(sl?.TotalInputTokens)} out {Tokens(sl?.TotalOutputTokens ?? transcriptOutput)} {Cost(sl?.CostUsd ?? transcriptCost)}";

    /// <summary>"5h 58% · wk 12%" from the status line's rate limits (Pro/Max, after the first reply), else the usage
    /// Claude Code cached in .claude.json, whose age goes in the tooltip.</summary>
    public static (string Text, string Tip) Usage(StatusLineData? sl, AccountInfo? account)
    {
        if (sl is { FiveHourUsedPercent: not null } or { SevenDayUsedPercent: not null })
            return ($"5h {Percent(sl.FiveHourUsedPercent)} · wk {Percent(sl.SevenDayUsedPercent)}", "Plan usage from Claude Code's status line");
        return ($"5h {Percent(account?.FiveHour?.Utilization)} · wk {Percent(account?.SevenDay?.Utilization)}",
            account?.UsageFetchedAt is { } at ? $"Cached by Claude Code, as of {at.ToLocalTime():yyyy-MM-dd HH:mm}" : "No usage data");
    }
}
