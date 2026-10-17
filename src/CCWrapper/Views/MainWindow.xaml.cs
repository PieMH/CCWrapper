using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    bool _polling, _confirmedEnd, _resuming;

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

        UpdateCenter();
        Bar.CompactRequested += Compose.Prefill;
        TerminalView.CloseRequested += (_, session) => CloseSession(session);
        _state.OpenSessions.CollectionChanged += (_, _) => UpdateCenter();
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

    // ---- sessions: a tab card per open session (SessionTabs), and at most one read-only card ----

    /// <summary>The center shows the active card's terminal, else the read-only view, else the empty state. WPF cannot draw
    /// over WebView2, so whatever is not shown is collapsed.</summary>
    void UpdateCenter()
    {
        var live = _state.ActiveSession is not null;
        var readOnly = !live && _elsewhere is not null;
        TerminalView.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        ElsewhereState.Visibility = readOnly ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = !live && !readOnly ? Visibility.Visible : Visibility.Collapsed;
        Tabs.ShowReadOnly(_elsewhere?.Title, readOnly);
    }

    /// <summary>Starts claude.exe for the session in a new card and makes it the active one.</summary>
    async Task OpenSessionAsync(LiveSession session, LaunchOptions options)
    {
        TerminalView.Visibility = Visibility.Visible; // the page sizes a new terminal while it is visible
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
            Dialog.Show(this, ex.Message, "Could not start the session", MessageBoxButton.OK, MessageBoxImage.Error);
            CloseSession(session);
        }
    }

    internal async Task NewSessionAsync()
    {
        var dialog = new NewSessionDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } options)
            await OpenSessionAsync(new LiveSession(_state.Account) { Title = options.Name }, options);
    }

    /// <summary>Shows the live read-only transcript of a session open in another claude process, as the read-only card.</summary>
    void ShowElsewhere(SessionEntry entry, string? query = null)
    {
        _elsewhere = entry;
        ElsewhereTitle.Text = entry.Title;
        ElsewhereWhere.Text = $"  is open in {(entry.Running is { } r ? ProcessTree.Describe(r.Pid) : "another terminal")}: read-only, updates live.";
        ElsewhereView.Content = new TranscriptView(entry.Path, query); // the previous view stops following when unloaded
        ShowReadOnlyCard();
    }

    /// <summary>Brings the read-only card forward; no live card is active while it shows.</summary>
    internal void ShowReadOnlyCard()
    {
        if (_elsewhere is null) return;
        _state.ActiveSession = null;
        UpdateCenter();
    }

    /// <summary>Removes the read-only card; the last live card comes forward when none is.</summary>
    internal void CloseElsewhere()
    {
        _elsewhere = null;
        ElsewhereView.Content = null;
        if (_state.ActiveSession is null && _state.OpenSessions.Count > 0) Activate(_state.OpenSessions[^1]);
        UpdateCenter();
    }

    /// <summary>The tab bar's search: the find bar over the terminal, or the read-only transcript's search box.</summary>
    internal void OpenFind()
    {
        if (_state.ActiveSession is not null) TerminalView.OpenFind();
        else (ElsewhereView.Content as TranscriptView)?.FocusSearch();
    }

    /// <summary>Resumes the session here. Still open elsewhere: says where and offers to close it there first (the claude
    /// process is ended, as closing its terminal would; the conversation so far is saved).</summary>
    async void OnResumeHere(object sender, RoutedEventArgs e)
    {
        if (_elsewhere is not { } entry || _resuming) return;
        if (entry.Cwd is null || !Directory.Exists(entry.Cwd)) { FolderGone(entry); return; } // before anything is closed elsewhere
        _resuming = true;
        SetResumeBusy("Checking…");
        try
        {
            var holders = await HoldersAsync(entry.Id, TimeSpan.FromSeconds(3)); // a terminal closed just now may still be shutting down
            if (holders.Count > 0)
            {
                var where = string.Join("\n", holders.Select(h => "    " + ProcessTree.Describe(h.Pid)));
                var busy = holders.Any(h => h.Status is SessionStatus.Busy or SessionStatus.Waiting)
                    ? "\n\nClaude is working or waiting for an answer there: that stops." : "";
                if (Dialog.Show(this, $"“{entry.Title}” is still open in:\n{where}\n\nClose it there and resume it here? The conversation so far is saved.{busy}",
                        "Resume here", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                SetResumeBusy("Closing it there…");
                foreach (var h in holders) ProcessTree.End(h.Pid);
                if ((holders = await HoldersAsync(entry.Id, TimeSpan.FromSeconds(5))).Count > 0)
                {
                    Dialog.Show(this, $"It could not be closed:\n{string.Join("\n", holders.Select(h => "    " + ProcessTree.Describe(h.Pid)))}\n\nClose it there, then try again.",
                        "Resume here", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            SetResumeBusy("Opening…");
            await OpenEntry(entry with { Running = null });
        }
        finally
        {
            _resuming = false;
            SetResumeBusy(null);
        }
    }

    /// <summary>Resume here at work: greyed and not clickable, a gradient sweeping along its bottom edge (a still bar when
    /// Windows animations are off) and what it is doing. Null puts it back.</summary>
    void SetResumeBusy(string? doing)
    {
        var busy = doing is not null;
        ResumeLabel.Text = doing ?? "Resume here";
        ResumeLabel.Opacity = busy ? 0.6 : 1;
        ResumeButton.IsHitTestVisible = !busy;
        CopyButton.IsEnabled = !busy;
        ResumeSweepBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ResumeSweep.BeginAnimation(TranslateTransform.XProperty, busy && SystemParameters.ClientAreaAnimation
            ? new DoubleAnimation(-ResumeSweepBar.Width, ResumeButton.ActualWidth, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    /// <summary>The live claude processes of this account that have the session open, waiting up to <paramref name="wait"/> for them to go.</summary>
    async Task<List<RunningSession>> HoldersAsync(string sessionId, TimeSpan wait)
    {
        for (var until = DateTime.UtcNow + wait; ; await Task.Delay(300))
        {
            var holders = (await Task.Run(() => SessionCatalog.Running(_state.Account))).Where(r => r.SessionId == sessionId).ToList();
            if (holders.Count == 0 || DateTime.UtcNow >= until) return holders;
        }
    }

    async void OnContinueAsCopy(object sender, RoutedEventArgs e)
    {
        if (_elsewhere is not { } entry || _resuming) return;
        if (entry.Cwd is not { } cwd || !Directory.Exists(cwd)) { FolderGone(entry); return; }
        CloseElsewhere();
        await OpenSessionAsync(new LiveSession(_state.Account) { Title = entry.Title }, new LaunchOptions(cwd, entry.Id, Fork: true));
    }

    void FolderGone(SessionEntry entry) =>
        Dialog.Show(this, $"The session's folder does not exist any more:\n{entry.Cwd ?? "(unknown)"}", "Cannot resume", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Brings an open session's card forward.</summary>
    internal void Activate(LiveSession session)
    {
        _state.ActiveSession = session; // first: the terminal is visible before it is shown and fitted
        TerminalView.Show(session);
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

    /// <summary>Already open here: its card comes forward. Open in another claude process: the read-only card. Otherwise it
    /// is resumed in its folder in a new card; nothing open is closed.</summary>
    public async Task OpenEntry(SessionEntry entry, string? query = null)
    {
        if (ShellModel.Match(_state.OpenSessions, entry.Running?.Pid, entry.Id) is { } open) { Activate(open); return; }
        if (entry.Running is not null) { ShowElsewhere(entry, query); return; }
        if (entry.Cwd is null || !Directory.Exists(entry.Cwd)) { FolderGone(entry); return; }
        if (_elsewhere?.Id == entry.Id) CloseElsewhere(); // its read-only card becomes this live one
        await OpenSessionAsync(new LiveSession(_state.Account) { Title = entry.Title }, new LaunchOptions(entry.Cwd, entry.Id));
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
        return n == 0 || Dialog.Show(this, $"{what} ends {n} busy or waiting session(s); they stay resumable.\n\nContinue?",
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
        UpdateCenter();
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
