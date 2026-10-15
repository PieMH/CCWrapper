using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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

    BarSource? _source;
    BarData? _data;
    LiveSession? _session;
    TerminalHost? _host;
    (LiveSession Session, string? Mode) _screenMode;
    string? _model, _effort, _mode;
    bool _switching;

    public BottomBar()
    {
        InitializeComponent();
        _ = new SessionFollower<BarSource, BarData>(this, s => _source = new BarSource(s), r => r.Read(), async d => { _data = d; await ReadScreenModeAsync(); Render(); });
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

        var reason = _switching ? "Switching mode…" : TuiInput.BlockedReason(s);
        foreach (var b in new[] { ModelButton, EffortButton, ModeButton })
        {
            b.IsEnabled = reason is null;
            b.ToolTip = reason;
        }
        CtxText.Text = BarFormat.Ctx(sl, d?.ContextTokens);
        SpendText.Text = BarFormat.Spend(sl, d?.OutputTokens, d?.CostUsd);
        (UsageText.Text, UsageText.ToolTip) = BarFormat.Usage(sl, d?.Account);
    }

    void OnModel(object sender, RoutedEventArgs e) => Open(ModelButton, Models, v => _model?.Contains(v, StringComparison.OrdinalIgnoreCase) == true, v => Send("/model " + v));

    void OnEffort(object sender, RoutedEventArgs e) => Open(EffortButton, Efforts, v => v == _effort, v => Send("/effort " + v));

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

    async Task Send(string line)
    {
        if (_session is { } s) await TuiInput.SubmitAsync(s, line);
    }

    /// <summary>The mode in the terminal's footer (see <see cref="BarFormat.ModeFromScreen"/>); null if there is no terminal or no footer.</summary>
    async Task ReadScreenModeAsync()
    {
        var s = _session;
        if (s is null) return;
        _host ??= Window.GetWindow(this) is { } w ? FindHost(w) : null;
        string? screen = null;
        try { if (_host is not null) screen = await _host.ReadTextAsync(s); }
        catch (Exception) { } // WebView2 not up yet or gone: this runs from an async void timer callback, so it must not throw
        _screenMode = (s, BarFormat.ModeFromScreen(screen));
    }

    static TerminalHost? FindHost(DependencyObject node)
    {
        if (node is TerminalHost host) return host;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindHost(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    async Task<string?> CurrentModeAsync(BarSource src)
    {
        _data = await Task.Run(src.Read);
        await ReadScreenModeAsync();
        return _screenMode.Session == _session ? _screenMode.Mode ?? _data.Mode : _data.Mode;
    }

    /// <summary>Shift+Tab until the permission mode is <paramref name="target"/>: at most 6 presses, each waiting up to
    /// ~3 s for the mode to change. If one never shows, stops and leaves the real mode on display.</summary>
    async Task SetModeAsync(string target)
    {
        var s = _session;
        if (s is null || _source is not { } src || src.Session != s || TuiInput.BlockedReason(s) is not null) return;
        _switching = true;
        Render();
        try
        {
            var mode = await CurrentModeAsync(src);
            for (var presses = 0; mode != target && presses < 6 && s.SendKeys(ShiftTab); presses++)
            {
                var before = mode;
                for (var wait = 0; wait < 20 && mode == before; wait++)
                {
                    await Task.Delay(150);
                    mode = await CurrentModeAsync(src);
                }
                if (mode == before) break;
            }
        }
        finally
        {
            _switching = false;
            Render();
        }
    }
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

    /// <summary>"ctx 202k/1M 20%". Used tokens are the transcript's last turn, else window x percent; the percent is the status line's, else used / window.</summary>
    public static string Ctx(StatusLineData? sl, long? lastTurnTokens)
    {
        var size = sl?.ContextWindowSize;
        var pct = sl?.ContextUsedPercent;
        var used = lastTurnTokens ?? (size is { } w && pct is { } p ? (long)(w * p / 100) : null);
        pct ??= used is { } u && size is > 0 ? u * 100.0 / size : null;
        return used is null && size is null && pct is null ? "ctx " + None : $"ctx {Tokens(used)}/{Tokens(size)} {Percent(pct)}";
    }

    static readonly Dictionary<string, string> FooterModes = new()
    {
        ["manual mode"] = "default", ["plan mode"] = "plan", ["accept edits"] = "acceptEdits",
        ["bypass permissions"] = "bypassPermissions", ["don't ask"] = "dontAsk", ["auto mode"] = "auto",
    };

    /// <summary>The permission mode named by the TUI's footer ("⏵⏵ auto mode on (shift+tab to cycle)") among the last rows
    /// of the terminal text, or null. These are Claude Code's own indicator names.</summary>
    public static string? ModeFromScreen(string? screen)
    {
        if (screen is null) return null;
        foreach (var line in screen.Split('\n').TakeLast(10).Reverse())
            if (Regex.Match(line, @"^\W*(manual mode|plan mode|accept edits|bypass permissions|don't ask|auto mode) on\b") is { Success: true } m)
                return FooterModes[m.Groups[1].Value];
        return null;
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
