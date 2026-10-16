using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;

namespace CCWrapper.Views;

public partial class MainWindow : Window, IAppCommands
{
    readonly AppState _state;
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _gitTimer = new() { Interval = TimeSpan.FromMilliseconds(400) }; // debounce
    readonly DispatcherTimer _gitRefresh = new() { Interval = TimeSpan.FromSeconds(10) }; // commits and branch switches in the session
    LiveSession? _watched; // the active session whose cwd the GitHub label follows
    SessionEntry? _elsewhere; // shown read-only in the center: open in another claude process
    string? _gitCwd;
    CancellationTokenSource? _gitCts;
    bool _polling, _confirmedEnd;

    public MainWindow(Account account)
    {
        _state = AppState.Current = new AppState(account, this); // before InitializeComponent: child views read it
        InitializeComponent();
        var letter = ShellModel.LetterOf(account.Letter);
        var color = (Color)ColorConverter.ConvertFromString(ShellModel.ColorOf(account.Letter));
        Title = $"CCWrapper · {letter} · {account.Label}";
        AccountLabel.Text = account.Label;
        BadgeText.Text = letter;
        Badge.Background = new SolidColorBrush(color);
        Taskbar.Overlay = BadgeImage(letter, color);

        ShowTerminal(false);
        TerminalView.CloseRequested += (_, session) => CloseSession(session);
        _state.OpenSessions.CollectionChanged += (_, _) => ShowTerminal(_state.OpenSessions.Count > 0);
        _state.PropertyChanged += OnStateChanged;
        _statusTimer.Tick += async (_, _) => await PollStatusAsync();
        _gitTimer.Tick += async (_, _) => await ReadGitAsync();
        _gitRefresh.Tick += async (_, _) => { if (_gitCwd is not null) await ReadGitAsync(); };
        Loaded += async (_, _) =>
        {
            _statusTimer.Start();
            _gitRefresh.Start();
            _ = ResumeFromArgsAsync();
            var info = await Task.Run(() => AccountReader.Read(account));
            PlanLabel.Text = ShellModel.PlanOf(info.OrganizationType) ?? "";
        };
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _gitRefresh.Stop();
            _gitCts?.Cancel();
        };
    }

    // ---- sessions ----

    /// <summary>WPF cannot draw over WebView2, so without a session the terminal is collapsed and a WPF panel takes its cell:
    /// the read-only view of a session open elsewhere, else the empty state.</summary>
    void ShowTerminal(bool visible)
    {
        TerminalView.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ElsewhereState.Visibility = !visible && _elsewhere is not null ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = !visible && _elsewhere is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Starts claude.exe for the session in a new tab and makes it the active one.</summary>
    async Task OpenSessionAsync(LiveSession session, LaunchOptions options)
    {
        ShowTerminal(true); // the page sizes a new terminal while it is visible
        try
        {
            var started = TerminalView.OpenAsync(session, options); // sets SessionId, Cwd and TranscriptPath before its first await
            _state.OpenSessions.Add(session);
            _state.ActiveSession = session;
            await started;
            if (_state.ActiveSession == session) TerminalView.Show(session); // moves the keyboard focus into the terminal
        }
        catch (Exception ex) // WebView2 cannot start; a failed claude.exe start is shown in the terminal instead
        {
            MessageBox.Show(this, ex.Message, "Could not start the session", MessageBoxButton.OK, MessageBoxImage.Error);
            CloseSession(session);
        }
    }

    internal async Task NewSessionAsync()
    {
        var dialog = new NewSessionDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } options && ClearCenter())
            await OpenSessionAsync(new LiveSession(_state.Account) { Title = options.Name }, options);
    }

    /// <summary>The center shows one session at a time: ends the one there (asking first when it is busy or waiting; it stays
    /// resumable) and drops a read-only view. False when the user declines.</summary>
    bool ClearCenter()
    {
        if (!ConfirmEnd("Opening another session", _state.OpenSessions)) return false;
        foreach (var s in _state.OpenSessions.ToList()) CloseSession(s);
        ShowElsewhere(null);
        return true;
    }

    /// <summary>Puts the live read-only transcript of a session open in another claude process in the center, or removes it (null).</summary>
    void ShowElsewhere(SessionEntry? entry, string? query = null)
    {
        _elsewhere = entry;
        ElsewhereTitle.Text = entry?.Title ?? "";
        ElsewhereView.Content = entry is null ? null : new TranscriptView(entry.Path, query); // the old view stops following when unloaded
        ShowTerminal(_state.OpenSessions.Count > 0);
    }

    async void OnResumeHere(object sender, RoutedEventArgs e)
    {
        if (_elsewhere is not { } entry) return;
        var running = await Task.Run(() => SessionCatalog.Running(_state.Account));
        if (running.Any(r => r.SessionId == entry.Id))
            MessageBox.Show(this, "It is still open in another terminal. Close it there first, or continue here as a copy.",
                "Resume here", MessageBoxButton.OK, MessageBoxImage.Information);
        else await OpenEntry(entry with { Running = null });
    }

    async void OnContinueAsCopy(object sender, RoutedEventArgs e)
    {
        if (_elsewhere is not { } entry) return;
        if (entry.Cwd is not { } cwd || !Directory.Exists(cwd)) { FolderGone(entry); return; }
        ShowElsewhere(null);
        await OpenSessionAsync(new LiveSession(_state.Account) { Title = entry.Title }, new LaunchOptions(cwd, entry.Id, Fork: true));
    }

    void FolderGone(SessionEntry entry) =>
        MessageBox.Show(this, $"The session's folder does not exist any more:\n{entry.Cwd ?? "(unknown)"}", "Cannot resume", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Shows the open session (with one session per window, that is the one already in the center).</summary>
    internal void Activate(LiveSession session)
    {
        TerminalView.Show(session);
        _state.ActiveSession = session;
    }

    /// <summary>Kills the session's claude.exe, removes its tab (it stays resumable from the list) and shows a neighbor.</summary>
    internal void CloseSession(LiveSession session)
    {
        var i = _state.OpenSessions.IndexOf(session);
        TerminalView.Close(session);
        _state.OpenSessions.Remove(session);
        if (_state.ActiveSession != session) return;
        _state.ActiveSession = _state.OpenSessions.Count == 0 ? null : _state.OpenSessions[Math.Max(0, i - 1)];
        if (_state.ActiveSession is { } next) TerminalView.Show(next);
    }

    /// <summary>Opens the session in the center, replacing what is there (see <see cref="ClearCenter"/>). Already open: show it.
    /// Running in another claude process: its live transcript, read-only. Otherwise resumed in its folder.</summary>
    public async Task OpenEntry(SessionEntry entry, string? query = null)
    {
        if (ShellModel.Match(_state.OpenSessions, entry.Running?.Pid, entry.Id) is { } open) { Activate(open); return; }
        if (entry.Running is null && (entry.Cwd is null || !Directory.Exists(entry.Cwd))) { FolderGone(entry); return; }
        if (!ClearCenter()) return;
        if (entry.Running is not null) ShowElsewhere(entry, query);
        else await OpenSessionAsync(new LiveSession(_state.Account) { Title = entry.Title }, new LaunchOptions(entry.Cwd!, entry.Id));
    }

    // `--resume <id>`: set by the picker when it restarts this process as another account.
    async Task ResumeFromArgsAsync()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => a.Equals("--resume", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return;
        var id = args[i + 1];
        var entry = await Task.Run(() => SessionCatalog.List(_state.Account).FirstOrDefault(s => s.Id == id));
        if (entry is not null) await OpenEntry(entry);
    }

    /// <summary>False when the user declines to end busy or waiting sessions.</summary>
    internal bool ConfirmEnd(string what, IEnumerable<LiveSession> sessions)
    {
        var n = sessions.Count(s => s.Status is SessionStatus.Busy or SessionStatus.Waiting);
        return n == 0 || MessageBox.Show(this, $"{what} ends {n} busy or waiting session(s); they stay resumable.\n\nContinue?",
            "CCWrapper", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_confirmedEnd && !ConfirmEnd("Closing the window", _state.OpenSessions)) e.Cancel = true;
    }

    // ---- top bar ----

    void OnSwitch(object sender, RoutedEventArgs e)
    {
        if (!ConfirmEnd("Switching account", _state.OpenSessions)) return;
        _confirmedEnd = true; // the restart closes this window: do not ask again
        new AccountPicker(_state.Account) { Owner = this }.ShowDialog();
        _confirmedEnd = false;
    }

    async void OnNewSession(object sender, RoutedEventArgs e) => await NewSessionAsync();

    void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppState.ActiveSession)) return;
        if (_watched is not null) _watched.PropertyChanged -= OnWatchedChanged;
        _watched = _state.ActiveSession;
        if (_watched is not null) _watched.PropertyChanged += OnWatchedChanged;
        ScheduleGit();
    }

    // May fire off the UI thread.
    void OnWatchedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LiveSession.Cwd) or nameof(LiveSession.StatusLine)) Dispatcher.InvokeAsync(ScheduleGit);
    }

    void ScheduleGit()
    {
        var cwd = _watched is { } w ? w.StatusLine?.Cwd ?? w.Cwd : null;
        if (cwd == _gitCwd) return;
        _gitCwd = cwd;
        _gitTimer.Stop();
        _gitTimer.Start();
    }

    async Task ReadGitAsync()
    {
        _gitTimer.Stop();
        _gitCts?.Cancel();
        var cts = _gitCts = new CancellationTokenSource();
        GitInfo info;
        try { info = await GitInfo.ReadAsync(_gitCwd, cts.Token); }
        catch (OperationCanceledException) { return; } // a newer read is on its way
        GitAccount.Text = info.InRepo ? info.Label : "git: none";
        GitBranchSep.Visibility = info.Branch is null ? Visibility.Collapsed : Visibility.Visible;
        GitBranch.Text = info.Branch ?? "";
        GitAhead.Text = info.Ahead is > 0 ? $"↑{info.Ahead}" : "";
        GitBehind.Text = info.Behind is > 0 ? $"↓{info.Behind}" : "";
        GitChip.ToolTip = ShellModel.GitTip(info);
    }

    /// <summary>The letter on a color circle, the taskbar overlay that tells the windows of different accounts apart.</summary>
    static BitmapSource BadgeImage(string letter, Color color)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(color), null, new Point(16, 16), 16, 16);
            var text = new FormattedText(letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 22,
                new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), 1.0);
            dc.DrawText(text, new Point(16 - text.Width / 2, 16 - text.Height / 2));
        }
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // ---- status ----

    /// <summary>Copies the claude registry's status of each open session onto it (matched by pid, so a /clear keeps the tab's status); a session turning Waiting flashes the taskbar.</summary>
    async Task PollStatusAsync()
    {
        if (_polling || _state.OpenSessions.Count == 0) return;
        _polling = true;
        try
        {
            var running = await Task.Run(() => SessionCatalog.Running(_state.Account)); // reads files
            ShellModel.Rebind(_state.OpenSessions.ToList(), running, ProcessTree.Parents);
            foreach (var (session, registry) in ShellModel.MapStatus(_state.OpenSessions.ToList(), running))
            {
                ShellModel.Adopt(session, registry); // /clear or /resume in the TUI: the pid stays, the session id changes
                var was = session.Status;
                session.Status = registry.Status;
                session.WaitingFor = registry.WaitingFor;
                if (registry.Status == SessionStatus.Waiting && was != SessionStatus.Waiting) FlashAttention(session);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // registry changing: the next poll catches up
        finally { _polling = false; }
    }

    // ---- IAppCommands ----

    public void ShowTranscript(LiveSession session, string? query = null)
    {
        if (session.TranscriptPath is { } path)
            ShowTranscript(path, session.Title ?? session.SessionId ?? "Transcript", query);
    }

    public void ShowTranscript(string transcriptPath, string title, string? query = null) =>
        new Window { Title = title, Width = 900, Height = 700, Owner = this, Content = new TranscriptView(transcriptPath, query) }.Show();

    public void OpenInBrowser(string url)
    {
        RightTabs.SelectedItem = BrowserTab;
        Browser.Navigate(url);
    }

    /// <summary>Taskbar flash until the window is brought forward; a short flash when the window is active but the session is a background one.</summary>
    public void FlashAttention(LiveSession session)
    {
        if (IsActive && _state.ActiveSession == session) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(), Hwnd = handle,
            Flags = IsActive ? FlashTray : FlashTray | FlashTimerNoForeground, Count = IsActive ? 3u : 0,
        };
        FlashWindowEx(ref info);
    }

    const uint FlashTray = 0x2, FlashTimerNoForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    struct FlashInfo
    {
        public uint Size;
        public nint Hwnd;
        public uint Flags, Count, Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FlashWindowEx(ref FlashInfo info);
}
