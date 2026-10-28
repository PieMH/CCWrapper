using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;

namespace CCWrapper.Views;

public partial class BottomBar : UserControl
{
    static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    static readonly string[] Modes = ["default", "acceptEdits", "plan", "auto"];
    const string ShiftTab = "\x1b[Z";
    const string SavedAsDefault = "Claude Code saves this as the account's default";

    BarData? _data;
    LiveSession? _session;
    (LiveSession Session, string? Mode) _screenMode;
    string? _model, _effort, _mode;
    AccountInfo? _account; // the window's account, read at start and after each /usage check
    bool _checkingUsage;
    Func<string> _usageTip = () => "";
    (double? Five, double? Week, DateTimeOffset? FiveReset, DateTimeOffset? WeekReset) _usageNow;
    DateTime _usageClosedAt, _choiceClosedAt;
    Button? _choiceFor; // the chip the model/effort/mode popup was last opened for: its own reopen guard
    IInputElement? _popupFocus; // what had focus before a usage or chip button took it, for Esc to give back
    (LiveSession Session, bool? On) _screenUltra;
    readonly Dictionary<LiveSession, bool> _ultraSent = []; // shortcut: the last /effort ultracode sent per session, for when the footer does not say; lost on restart
    Window? _window;
    CompactPrefs _compact = CompactPrefs.Load();
    readonly DispatcherTimer _usageTimer = new() { Interval = TimeSpan.FromMinutes(30) };

    /// <summary>The Compact hint's button: the command for the message box, which MainWindow routes there. Never sent.</summary>
    public event Action<string>? CompactRequested;

    public BottomBar()
    {
        InitializeComponent();
        _usageTimer.Tick += async (_, _) => await CheckUsageAsync(force: false);
        Loaded += async (_, _) =>
        {
            UsageProbe.Refreshed += OnUsageRefreshed;
            AppUpdate.Changed += ShowUpdate;
            ShowUpdate();
            CompactPrefs.Saved += OnCompactPrefs;
            OnCompactPrefs();
            _usageTimer.Start();
            if ((_window = Window.GetWindow(this)) is { } w)
            {
                w.PreviewMouseDown += OnWindowMouseDown;
                w.Deactivated += ClosePopups;
                w.LocationChanged += ClosePopups;
                w.SizeChanged += ClosePopups;
            }
            await ReadAccountAsync();
            await CheckUsageAsync(force: false); // skipped when the start screen just checked
        };
        Unloaded += (_, _) =>
        {
            UsageProbe.Refreshed -= OnUsageRefreshed;
            AppUpdate.Changed -= ShowUpdate;
            CompactPrefs.Saved -= OnCompactPrefs;
            _usageTimer.Stop();
            if (_window is { } w)
            {
                w.PreviewMouseDown -= OnWindowMouseDown;
                w.Deactivated -= ClosePopups;
                w.LocationChanged -= ClosePopups;
                w.SizeChanged -= ClosePopups;
            }
        };
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
        ModelValue.Text = BarFormat.ModelLabel(_model) ?? BarFormat.None;
        EffortValue.Text = _effort ?? BarFormat.None;
        ModeValue.Text = _mode ?? BarFormat.None;

        var reason = TuiInput.BlockedReason(s);
        foreach (var b in new[] { ModelButton, EffortButton, ModeButton }) b.IsEnabled = reason is null;
        ModelButton.ToolTip = EffortButton.ToolTip = reason ?? SavedAsDefault;
        ModeButton.ToolTip = reason;
        var (ctx, ctxPercent) = BarFormat.CtxParts(sl, d?.ContextTokens);
        CtxText.Text = ctx;
        Meter(CtxBar, ctxPercent, warn: false);
        var hint = _compact.Enabled && s is not null && ctxPercent >= _compact.Threshold;
        CompactHint.Visibility = hint ? Visibility.Visible : Visibility.Collapsed;
        CompactText.Text = $"Context {BarFormat.Percent(ctxPercent)}";
        CompactButton.ToolTip = "Put " + _compact.Command + " in the message box (not sent)";
        SpendText.Text = BarFormat.Spend(sl, d?.OutputTokens, d?.CostUsd);
        var account = BarFormat.Newer(_account, d?.Account);
        var usage = BarFormat.Usage(sl, account); // shown with no session open too
        FiveHourText.Text = BarFormat.Percent(usage.FiveHour);
        WeekText.Text = BarFormat.Percent(usage.Week);
        Meter(FiveHourBar, usage.FiveHour, warn: true);
        Meter(WeekBar, usage.Week, warn: true);
        _usageTip = () => BarFormat.Usage(sl, account).Tip;
        _usageNow = (usage.FiveHour, usage.Week, usage.FiveReset, usage.WeekReset);
        UsageGroup.ToolTip ??= usage.Tip; // non-null so ToolTipOpening fires; OnUsageTip makes the real text
        RefreshButton.IsEnabled = !_checkingUsage;
        RefreshButton.ToolTip = _checkingUsage ? "Checking plan usage…" : "Check plan usage now (runs /usage hidden)";
    }

    // Built when it opens: the "in 2h 10m" of the reset times is relative to now, not to the last render.
    void OnUsageTip(object sender, ToolTipEventArgs e) => UsageGroup.ToolTip = _usageTip();

    // OnWindowMouseDown closes the popup on the mouse-down of a click on the button itself, so a click right after a close must not reopen it.
    void OnUsageClick(object sender, RoutedEventArgs e) => OpenUsage();

    // Any press in this window but in the popup closes it. The popup's own mouse events route through the window too. A click in a
    // WebView2 (terminal, Browser panel) never reaches WPF: it takes the focus, and OnUsageFocusWithin closes the popup then.
    void OnWindowMouseDown(object sender, MouseButtonEventArgs e) => ClosePopups(sender, e);

    void OnUsageFocusWithin(object sender, DependencyPropertyChangedEventArgs e) { if (!UsagePanel.IsKeyboardFocusWithin) UsagePopup.IsOpen = false; }

    // The model/effort/mode popup closes the same ways; a press on a popup's own panel leaves it open.
    void ClosePopups(object? sender, EventArgs e)
    {
        if (e is not MouseButtonEventArgs || !UsagePanel.IsMouseOver) UsagePopup.IsOpen = false;
        if (e is not MouseButtonEventArgs || !ChoicePanel.IsMouseOver) ChoicePopup.IsOpen = false;
    }

    void OpenUsage()
    {
        if (UsagePopup.IsOpen || DateTime.UtcNow - _usageClosedAt < TimeSpan.FromMilliseconds(250)) return;
        Keyboard.Focus(UsageGroup); // a no-op for a click (the Button focused itself), but it also moves Win32 focus off the WebView2 for Enter and Space
        UsagePopup.HorizontalOffset = UsageGroup.ActualWidth - UsagePanel.Width; // right edges line up; WPF keeps it on screen
        var account = AppState.Current?.Account;
        var now = _usageNow;
        ShowUsage(now, null, loading: account is not null);
        UsagePopup.IsOpen = true;
        // The popup window is WS_EX_NOACTIVATE, so Win32 focus stays in the main window, on the button: the WebView2 lost it when the button took focus. WPF focus moves into the popup once it exists.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (UsagePopup.IsOpen) Keyboard.Focus(UsagePanel); });
        if (account is null) return;
        // The transcripts are read off the UI thread; reopening within a minute gets the cached result at once.
        _ = Task.Run(() => UsageStats.Get(account, now.FiveReset, now.WeekReset)).ContinueWith(t =>
        {
            if (UsagePopup.IsOpen) ShowUsage(now, t.Result, loading: false);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // The button takes keyboard focus on mouse-down, before the click, so the element to give focus back to is the one it took it from.
    void OnChipFocus(object sender, KeyboardFocusChangedEventArgs e) { if (e.OldFocus is { } old && !ReferenceEquals(old, sender)) _popupFocus = old; }

    void OnUsageClosed(object? sender, EventArgs e) => _usageClosedAt = DateTime.UtcNow;

    // Esc is taken here, in the tunnelling phase. Win32 focus is on the main window (the button took it from the WebView2), so the key
    // message reaches WPF and never the terminal's WebView2 (and the TUI behind it, where Esc interrupts a running turn).
    void OnUsageKey(object sender, KeyEventArgs e) => EscClose(UsagePopup, e);

    void EscClose(Popup popup, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        popup.IsOpen = false;
        if (_popupFocus is not null) Keyboard.Focus(_popupFocus);
        e.Handled = true;
    }

    /// <summary>The popup: per window its bar, reset and tokens (in / out / cache) by model, then the week's tokens by hour of day.</summary>
    void ShowUsage((double? Five, double? Week, DateTimeOffset? FiveReset, DateTimeOffset? WeekReset) u, UsageStatsResult? stats, bool loading)
    {
        UsageBody.Children.Clear();
        var now = DateTimeOffset.Now;
        Section("5h session", u.Five, BarFormat.ResetText(u.FiveReset, now, alwaysDay: false), stats?.FiveHour, loading, first: true);
        Section("Weekly", u.Week, BarFormat.ResetText(u.WeekReset, now, alwaysDay: true), stats?.Week, loading, first: false);
        if (stats is null)
        {
            if (!loading) Add(Label("Token counts are not available.", "Muted", top: 10));
            return;
        }
        var hours = stats.Week.ByHour;
        var max = hours.Max();
        Add(Label("Week by hour of day (local time)" + (max > 0 ? $", peak {Array.IndexOf(hours, max):00}:00" : ""), "Muted", top: 12));
        const double stripHeight = 36;
        var strip = new Grid { Height = stripHeight, Margin = new Thickness(0, 4, 0, 0) };
        for (var h = 0; h < 24; h++)
        {
            strip.ColumnDefinitions.Add(new ColumnDefinition());
            var bar = new Border
            {
                Background = (Brush)FindResource("Accent"), CornerRadius = new CornerRadius(1), Margin = new Thickness(1, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = max > 0 ? Math.Max(hours[h] > 0 ? 2 : 0, stripHeight * hours[h] / max) : 0,
                Opacity = max > 0 && hours[h] == max ? 1 : 0.6, // the peak hour stands out
                ToolTip = $"{h:00}:00  {BarFormat.Tokens(hours[h])} tokens",
            };
            Grid.SetColumn(bar, h);
            strip.Children.Add(bar);
        }
        Add(strip);
        var axis = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        for (var h = 0; h < 24; h++)
        {
            axis.ColumnDefinitions.Add(new ColumnDefinition());
            if (h % 6 != 0) continue;
            var l = Label($"{h:00}", "Muted", top: 0);
            l.FontSize = 10;
            Grid.SetColumn(l, h);
            axis.Children.Add(l);
        }
        Add(axis);
        Add(Label($"Tokens = input + output + cache writes; cache reads are listed apart. Counted from {stats.Files} transcript file(s) written in the last 7 days, subagents included ({stats.Took.TotalMilliseconds:0} ms).", "Muted", top: 10, wrap: true));
    }

    void Section(string title, double? percent, string reset, WindowUsage? w, bool loading, bool first)
    {
        var head = new DockPanel { Margin = new Thickness(0, first ? 0 : 12, 0, 0) };
        var pct = Label(BarFormat.Percent(percent), percent >= 80 ? "Warn" : "Text", top: 0);
        pct.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(pct, Dock.Right);
        var name = Label(title, "Text", top: 0);
        name.FontWeight = FontWeights.SemiBold;
        head.Children.Add(pct);
        head.Children.Add(name);
        Add(head);
        // the fill is a share of the track: star columns do it without a size pass
        var p = Math.Clamp(percent ?? 0, 0, 100);
        var fill = new Grid();
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(p, GridUnitType.Star) });
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - p, GridUnitType.Star) });
        fill.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = (Brush)FindResource(p >= 80 ? "Warn" : "Accent") });
        Add(new Border { Style = (Style)FindResource("UsageTrack"), Child = fill });
        Add(Label(reset.Length > 0 ? "Window " + reset : "Reset time unknown", "Muted", top: 0));
        if (w is null)
        {
            if (loading) Add(Label("Reading transcripts…", "Muted", top: 4));
            return;
        }
        var t = w.Total;
        Add(Label(w.Messages == 0 ? "No tokens in this window"
            : $"{BarFormat.Tokens(t.Work)} tokens  ·  in {BarFormat.Tokens(t.Input)}  out {BarFormat.Tokens(t.Output)}  cache write {BarFormat.Tokens(t.CacheWrite)}  read {BarFormat.Tokens(t.CacheRead)}",
            "TextSoft", top: 6, wrap: true));
        var total = w.ByModel.Sum(m => m.Work);
        foreach (var (model, work) in w.ByModel.Take(4))
        {
            if (total == 0) break;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var share = Label($"{100.0 * work / total:0}%  {BarFormat.Tokens(work)}", "Muted", top: 0);
            DockPanel.SetDock(share, Dock.Right);
            row.Children.Add(share);
            row.Children.Add(Label(model is null ? BarFormat.None : model.Replace("claude-", ""), "TextSoft", top: 0));
            Add(row);
        }
    }

    void Add(UIElement e) => UsageBody.Children.Add(e);

    TextBlock Label(string text, string brush, double top, bool wrap = false) => new()
    {
        Text = text, Foreground = (Brush)FindResource(brush), Margin = new Thickness(0, top, 0, 0),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    /// <summary>Fills a meter's bar to <paramref name="percent"/> of its track; plan usage turns amber from 80%.</summary>
    void Meter(Border bar, double? percent, bool warn)
    {
        var p = Math.Clamp(percent ?? 0, 0, 100);
        bar.Width = ((FrameworkElement)bar.Parent).Width * p / 100;
        if (warn) bar.Background = (Brush)FindResource(p >= 80 ? "Warn" : "Accent");
    }

    void ShowUpdate()
    {
        var v = AppUpdate.ReadyVersion;
        UpdateButton.Visibility = v is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateText.Text = $"Update {v} ready";
    }

    /// <summary>The updater stops every process of the install folder, so other windows close too.</summary>
    void OnUpdate(object sender, RoutedEventArgs e)
    {
        var busy = AppState.Current.OpenSessions.Count(s => s.Status is SessionStatus.Busy or SessionStatus.Waiting);
        var others = AppUpdate.OtherInstances();
        var text = $"Restart now to install {AppUpdate.ReadyVersion}?";
        if (busy > 0) text += $"\n\nThis ends {busy} busy or waiting session(s); they stay resumable.";
        if (others > 0) text += $"\n\nThe other CCWrapper window(s) ({others}) close too, and their sessions end the same way.\n\nUnsent drafts in the other window(s) are lost if they have not noticed the update.";
        if (Dialog.Show(Window.GetWindow(this), text, "CCWrapper", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            AppUpdate.RestartNow(AppState.Current?.Account.Letter);
    }

    // The prefs are read here and when Settings saves them, not on each render.
    void OnCompactPrefs()
    {
        _compact = CompactPrefs.Load();
        Render();
    }

    void OnCompact(object sender, RoutedEventArgs e) => CompactRequested?.Invoke(_compact.Command);

    void OnRefresh(object sender, RoutedEventArgs e) => _ = CheckUsageAsync(force: true);

    /// <summary>Runs /usage hidden for the window's account (see <see cref="UsageProbe"/>); the Refreshed event re-reads it.</summary>
    async Task CheckUsageAsync(bool force)
    {
        if (AppState.Current?.Account is not { } account || _checkingUsage) return;
        _checkingUsage = true;
        Render();
        try { await UsageProbe.RefreshAsync(account, force); }
        finally
        {
            _checkingUsage = false;
            Render();
        }
    }

    // A pool thread; the start screen's checks of this account land here too.
    void OnUsageRefreshed(Account account)
    {
        if (account.Letter == AppState.Current?.Account.Letter) Dispatcher.InvokeAsync(ReadAccountAsync);
    }

    async Task ReadAccountAsync()
    {
        if (AppState.Current?.Account is not { } account) return;
        _account = await Task.Run(() => AccountReader.Read(account));
        Render();
    }

    // A refusal (e.g. text already in the terminal input) shows in the compose box's notice.
    void OnModel(object sender, RoutedEventArgs e) =>
        Open(ModelButton, BarFormat.Models.Select(m => (m.Alias, m.Label, m.Note)), v => _model?.Contains(v, StringComparison.OrdinalIgnoreCase) == true, v => TuiInput.SubmitAsync(_session, "/model " + v));

    void OnEffort(object sender, RoutedEventArgs e) => Open(EffortButton, Plain(Efforts), v => v == _effort, v => TuiInput.SubmitAsync(_session, "/effort " + v), ultracode: true);

    void OnMode(object sender, RoutedEventArgs e) => Open(ModeButton, Plain(Modes), v => v == _mode, SetModeAsync);

    static IEnumerable<(string, string, string?)> Plain(string[] values) => values.Select(v => (v, v, (string?)null));

    void OnChoiceFocusWithin(object sender, DependencyPropertyChangedEventArgs e) { if (!ChoicePanel.IsKeyboardFocusWithin) ChoicePopup.IsOpen = false; }

    void OnChoiceKey(object sender, KeyEventArgs e) => EscClose(ChoicePopup, e);

    void OnChoiceClosed(object? sender, EventArgs e) => _choiceClosedAt = DateTime.UtcNow;

    /// <summary>The model, effort and mode chooser: the usage popup's look and click-away behavior (see <see cref="OpenUsage"/>), above
    /// <paramref name="chip"/>. A row is a Button: hover, Up/Down (the body's directional navigation), Enter, an automation name. A pick
    /// closes the popup and gives focus back; the Ultracode switch row (effort only) stays open and flips back if the send is refused.</summary>
    void Open(Button chip, IEnumerable<(string Value, string Label, string? Note)> rows, Func<string, bool> isCurrent, Func<string, Task> pick, bool ultracode = false)
    {
        if (ReferenceEquals(_choiceFor, chip) && DateTime.UtcNow - _choiceClosedAt < TimeSpan.FromMilliseconds(250)) return; // the press that closed it
        _choiceFor = chip;
        Keyboard.Focus(chip); // moves Win32 focus off the WebView2, as in OpenUsage
        ChoiceBody.Children.Clear();
        Button? focus = null;
        foreach (var (value, label, note) in rows)
        {
            var current = isCurrent(value);
            var row = Row(note is null ? label : $"{label}, {note}", current ? "current" : null, out var dock);
            var check = new TextBlock { Style = (Style)FindResource("Icon"), Text = "", FontSize = 12, Foreground = (Brush)FindResource("Accent"), Visibility = current ? Visibility.Visible : Visibility.Hidden };
            DockPanel.SetDock(check, Dock.Right);
            dock.Children.Add(check);
            dock.Children.Add(Label(label, "Text", top: 0));
            if (note is not null)
            {
                var n = Label(note, "Muted", top: 0);
                n.FontSize = 11;
                n.Margin = new Thickness(8, 0, 0, 0);
                dock.Children.Add(n);
            }
            row.Click += async (_, _) =>
            {
                ChoicePopup.IsOpen = false;
                if (_popupFocus is not null) Keyboard.Focus(_popupFocus);
                await pick(value);
            };
            ChoiceBody.Children.Add(row);
            if (current || focus is null) focus = row;
        }
        if (ultracode) AddUltracode();
        ChoicePopup.PlacementTarget = chip;
        ChoicePopup.IsOpen = true;
        // As in OpenUsage: WPF focus moves into the popup once it exists.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (ChoicePopup.IsOpen) Keyboard.Focus(focus); });
    }

    Button Row(string name, string? state, out DockPanel dock)
    {
        dock = new DockPanel();
        var row = new Button { Style = (Style)FindResource("PopupRow"), Content = dock };
        AutomationProperties.SetName(row, state is null ? name : $"{name}, {state}");
        return row;
    }

    /// <summary>A separator and the Ultracode row. The switch is only a picture: the row is the Button, so click, Enter and Space work alike.</summary>
    void AddUltracode()
    {
        ChoiceBody.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Line"), Margin = new Thickness(4, 4, 4, 5) });
        var s = _session;
        var on = _screenUltra.Session == s && _screenUltra.On is { } seen ? seen : s is not null && _ultraSent.TryGetValue(s, out var sent) && sent;
        var row = Row("Ultracode", on ? "on" : "off", out var dock);
        var sw = new ToggleButton { Style = (Style)FindResource("Switch"), Width = 44, Height = 24, IsChecked = on, IsHitTestVisible = false, Focusable = false };
        DockPanel.SetDock(sw, Dock.Right);
        dock.Children.Add(sw);
        dock.Children.Add(Label("Ultracode", "Text", top: 0));
        void Show(bool v)
        {
            sw.IsChecked = v;
            AutomationProperties.SetName(row, "Ultracode, " + (v ? "on" : "off"));
        }
        var busy = false;
        row.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            var want = sw.IsChecked != true;
            Show(want);
            // A refusal shows in the compose box's notice, as for the other picks.
            var refusal = await TuiInput.SubmitAsync(s, $"/effort ultracode {(want ? "on" : "off")}");
            if (refusal is null && s is not null) _ultraSent[s] = want;
            else Show(!want);
            busy = false;
        };
        ChoiceBody.Children.Add(row);
    }

    /// <summary>The mode in the terminal's footer (see <see cref="BarFormat.ModeFromScreen"/>), also kept for display; null if
    /// there is no terminal or no footer.</summary>
    async Task<string?> ReadScreenModeAsync(LiveSession s)
    {
        var tail = s.Channel is IScreenChannel screen ? await screen.ReadTailAsync(BarFormat.FooterRows) : null;
        _screenMode = (s, BarFormat.ModeFromScreen(tail));
        _screenUltra = (s, BarFormat.UltracodeFromScreen(tail));
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
    public static string Ctx(StatusLineData? sl, long? lastTurnTokens) => "ctx " + CtxParts(sl, lastTurnTokens).Text;

    /// <summary><see cref="Ctx"/> without its "ctx " label, and the percent for the meter bar.</summary>
    public static (string Text, double? Percent) CtxParts(StatusLineData? sl, long? lastTurnTokens)
    {
        var size = sl?.ContextWindowSize;
        var pct = sl?.ContextUsedPercent;
        var used = size is { } w && pct is { } p ? (long)(w * p / 100) : lastTurnTokens;
        pct ??= used is { } u && size is > 0 ? u * 100.0 / size : null;
        return (used is null && size is null && pct is null ? None : $"{Tokens(used)}/{Tokens(size)} {Percent(pct)}", pct);
    }

    // shortcut: hand-kept, as Claude Code exposes no list of models and versions; upgrade if it does. Alias is the /model argument.
    public static readonly (string Alias, string Label, string? Note)[] Models =
    [
        ("opus", "Opus 5.5", null), ("sonnet", "Sonnet 5.5", null), ("haiku", "Haiku 5.5", null), ("fable", "Fable 5.1", "runs on usage credits"),
    ];

    static readonly Regex ModelId = new(@"^claude-([a-z]+)-(\d+)(?:-(\d{1,2}))?(?:-\d{8})?$");

    /// <summary>"claude-opus-5-5" gives "Opus 5.5", "claude-fable-5-1-20260101" gives "Fable 5.1"; null for any other shape.</summary>
    public static string? ModelName(string? id) =>
        id is not null && ModelId.Match(id) is { Success: true } m
            ? $"{char.ToUpperInvariant(m.Groups[1].Value[0])}{m.Groups[1].Value[1..]} {m.Groups[2].Value}{(m.Groups[3].Success ? "." + m.Groups[3].Value : "")}"
            : null;

    /// <summary>The versioned name of a bare alias ("opus" gives "Opus 5.5") or of a full id; anything else (a display name) as it is.</summary>
    public static string? ModelLabel(string? model) =>
        Models.Where(m => m.Alias.Equals(model, StringComparison.OrdinalIgnoreCase)).Select(m => m.Label).FirstOrDefault() ?? ModelName(model) ?? model;

    static readonly Dictionary<string, string> FooterModes = new()
    {
        ["manual mode"] = "default", ["plan mode"] = "plan", ["accept edits"] = "acceptEdits",
        ["bypass permissions"] = "bypassPermissions", ["don't ask"] = "dontAsk", ["auto mode"] = "auto",
    };

    /// <summary>How many of the screen's last non-blank rows <see cref="ModeFromScreen"/> looks at.</summary>
    public const int FooterRows = 10;

    /// <summary>The last non-blank rows of the terminal text, bottom row first.</summary>
    static List<string> FooterLines(string screen) => screen.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(FooterRows).Reverse().ToList();

    /// <summary>"Ultracode on" or "Ultracode off" in the footer rows (at a row's start or after a "·"), else null.
    /// shortcut: the footer's wording is a guess, as the screen text is not documented; the caller falls back to what it last sent.</summary>
    public static bool? UltracodeFromScreen(string? screen) =>
        screen is not null && FooterLines(screen).Select(l => Regex.Match(l, @"(?:^\W*|·\s*)ultracode (on|off)\b", RegexOptions.IgnoreCase)).FirstOrDefault(m => m.Success) is { } m
            ? m.Groups[1].Value.Equals("on", StringComparison.OrdinalIgnoreCase) : null;

    /// <summary>The permission mode named by the TUI's footer ("⏵⏵ auto mode on (shift+tab to cycle)") among the last non-blank
    /// rows of the terminal text, or null. These are Claude Code's own indicator names; the default mode shows none, only
    /// "? for shortcuts".</summary>
    public static string? ModeFromScreen(string? screen)
    {
        if (screen is null) return null;
        var rows = FooterLines(screen);
        foreach (var line in rows)
            if (Regex.Match(line, @"^\W*(manual mode|plan mode|accept edits|bypass permissions|don't ask|auto mode) on\b") is { Success: true } m)
                return FooterModes[m.Groups[1].Value];
        return rows.Any(l => Regex.IsMatch(l, @"^\s*\? for shortcuts")) ? "default" : null;
    }

    /// <summary>"in 1.2M out 48k $16.7": the status line's session totals, else the transcript's output and cost.</summary>
    public static string Spend(StatusLineData? sl, long? transcriptOutput, double? transcriptCost) =>
        $"in {Tokens(sl?.TotalInputTokens)} out {Tokens(sl?.TotalOutputTokens ?? transcriptOutput)} {Cost(sl?.CostUsd ?? transcriptCost)}";

    /// <summary>"5h 58% · wk 12%", from whichever is newer: the status line's rate limits (Pro/Max, after the first reply) or
    /// the usage Claude Code saved in .claude.json on its last /usage (see UsageProbe). Its source and time, and when each
    /// window resets, go in the tooltip (relative to <paramref name="now"/>); the two percents feed the meter bars.</summary>
    public static (string Text, string Tip, double? FiveHour, double? Week, DateTimeOffset? FiveReset, DateTimeOffset? WeekReset) Usage(StatusLineData? sl, AccountInfo? account, DateTimeOffset? now = null)
    {
        var saved = account?.UsageFetchedAt;
        var live = (sl is { FiveHourUsedPercent: not null } or { SevenDayUsedPercent: not null }) && (saved is null || sl.ReceivedAt > saved);
        var (five, week) = live ? (sl!.FiveHourUsedPercent, sl.SevenDayUsedPercent) : (account?.FiveHour?.Utilization, account?.SevenDay?.Utilization);
        var (fiveReset, weekReset) = live ? (sl!.FiveHourResetsAt, sl.SevenDayResetsAt) : (account?.FiveHour?.ResetsAt, account?.SevenDay?.ResetsAt);
        var tip = live ? $"Plan usage from Claude Code's status line, as of {sl!.ReceivedAt.ToLocalTime():HH:mm}"
            : saved is { } at ? $"Plan usage from Claude Code's /usage, as of {at.ToLocalTime():yyyy-MM-dd HH:mm}" : "No usage data";
        var t = now ?? DateTimeOffset.Now;
        tip += Reset("5h window", fiveReset, t, alwaysDay: false) + Reset("Weekly window", weekReset, t, alwaysDay: true);
        return ($"5h {Percent(five)} · wk {Percent(week)}", tip, five, week, fiveReset, weekReset);
    }

    /// <summary>"5h window resets 14:30 (in 2h 10m)", with the weekday when it is not today (always for <paramref name="alwaysDay"/>);
    /// empty when the time is unknown.</summary>
    static string Reset(string what, DateTimeOffset? at, DateTimeOffset now, bool alwaysDay) =>
        ResetText(at, now, alwaysDay) is { Length: > 0 } t ? $"\n{what} {t}" : "";

    /// <summary>"resets 14:30 (in 2h 10m)" or "reset was due 14:30"; empty when the time is unknown.</summary>
    public static string ResetText(DateTimeOffset? at, DateTimeOffset now, bool alwaysDay)
    {
        if (at is not { } r) return "";
        var local = r.ToLocalTime();
        var when = local.ToString(alwaysDay || local.Date != now.ToLocalTime().Date ? "ddd HH:mm" : "HH:mm", Inv);
        var left = r - now;
        if (left <= TimeSpan.Zero) return $"reset was due {when}";
        var span = left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h" : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m" : $"{Math.Max(1, left.Minutes)}m";
        return $"resets {when} (in {span})";
    }

    /// <summary>The account read whose saved usage is newer (a null one loses).</summary>
    public static AccountInfo? Newer(AccountInfo? a, AccountInfo? b) =>
        (b?.UsageFetchedAt ?? DateTimeOffset.MinValue) > (a?.UsageFetchedAt ?? DateTimeOffset.MinValue) ? b : a ?? b;
}
