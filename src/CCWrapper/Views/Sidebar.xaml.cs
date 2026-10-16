using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CCWrapper.Core;
using CCWrapper.Data;

namespace CCWrapper.Views;

/// <summary>Search, Settings menu, Customize, New session and the session list of this window's account.</summary>
public partial class Sidebar : UserControl
{
    readonly ObservableCollection<SessionRow> _rows = [];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    IReadOnlyList<SessionEntry> _catalog = [];
    IReadOnlyDictionary<string, SearchHit>? _hits; // full-text results while shown, else the instant filter applies
    string? _query;
    CancellationTokenSource? _search;
    bool _refreshing;
    Window? _customize;

    static AppState State => AppState.Current;
    MainWindow Main => (MainWindow)Window.GetWindow(this);

    public Sidebar()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        State.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.ActiveSession)) Rebuild(); };
        State.OpenSessions.CollectionChanged += OnOpenChanged;
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += (_, _) => { _timer.Start(); _ = RefreshAsync(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    // ---- list ----

    void OnOpenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (LiveSession s in e.OldItems ?? Array.Empty<LiveSession>()) s.PropertyChanged -= OnLiveChanged;
        foreach (LiveSession s in e.NewItems ?? Array.Empty<LiveSession>()) s.PropertyChanged += OnLiveChanged;
        Rebuild();
        _ = RefreshAsync(); // a tab opened or closed: its transcript and running state changed too
    }

    // May fire off the UI thread.
    void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LiveSession.Status) or nameof(LiveSession.WaitingFor) or nameof(LiveSession.Title)
            or nameof(LiveSession.SessionId) or nameof(LiveSession.Cwd))
            Dispatcher.InvokeAsync(Rebuild);
    }

    async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _catalog = await Task.Run(() => SessionCatalog.List(State.Account)); // reads and probes transcript files
            Rebuild();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a transcript vanished mid-scan: next tick
        finally { _refreshing = false; }
    }

    /// <summary>Updates the rows in place, so the scroll position survives the refreshes.</summary>
    void Rebuild()
    {
        var rows = SessionRow.Build(_catalog, State.OpenSessions, State.ActiveSession, DateTimeOffset.Now, Search.Text, _hits);
        for (var i = 0; i < rows.Count; i++)
        {
            if (i >= _rows.Count) _rows.Add(rows[i]);
            else if (_rows[i] != rows[i]) _rows[i] = rows[i];
        }
        while (_rows.Count > rows.Count) _rows.RemoveAt(_rows.Count - 1);
        Empty.Text = rows.Count > 0 ? "" : Search.Text.Length > 0 ? "No matching sessions" : "No sessions yet";
    }

    async Task OpenAsync(SessionRow row)
    {
        if (row.Live is { } live) Main.Activate(live);
        else if (row.Entry is { } entry) await Main.OpenEntry(entry, _hits is null ? null : _query);
    }

    async void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: SessionRow row }) await OpenAsync(row);
    }

    async void OnListKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedItem is SessionRow row) await OpenAsync(row);
    }

    void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SessionRow { Live: { } live } } && Main.ConfirmEnd("Closing this session", [live]))
            Main.CloseSession(live);
    }

    // ---- search ----

    void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        CancelSearch(); // typing leaves the full-text results and filters instantly again
        _hits = null;
        SearchStatus.Visibility = Visibility.Collapsed;
        Rebuild();
    }

    async void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Search.Clear(); e.Handled = true; }
        else if (e.Key == Key.Enter && Search.Text.Trim() is { Length: > 0 } query) { e.Handled = true; await SearchAsync(query); }
    }

    void CancelSearch()
    {
        _search?.Cancel();
        _search = null;
    }

    async Task SearchAsync(string query)
    {
        CancelSearch();
        var cts = _search = new CancellationTokenSource();
        SearchStatus.Text = "Searching…";
        SearchStatus.Visibility = Visibility.Visible;
        try
        {
            var hits = await Task.Run(() => SessionSearch.SearchAccount(State.Account, query, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            _hits = hits.GroupBy(h => h.SessionId).ToDictionary(g => g.Key, g => g.First());
            _query = query;
            SearchStatus.Text = hits.Count == 0 ? "No matches" : $"{hits.Count} matching session(s) · Esc clears";
            Rebuild();
        }
        catch (OperationCanceledException) { } // typed or searched again meanwhile
    }

    // ---- buttons ----

    void OnSettings(object sender, RoutedEventArgs e)
    {
        SettingsMenu.PlacementTarget = SettingsButton;
        SettingsMenu.IsOpen = true;
    }

    // The slash commands need a terminal that takes input: not while a prompt or dialog is open (the Enter would answer it).
    void OnSettingsOpened(object sender, RoutedEventArgs e)
    {
        foreach (var item in SettingsMenu.Items.OfType<MenuItem>().Where(m => m.Tag is string t && t.StartsWith('/')))
            item.IsEnabled = TuiInput.BlockedReason(State.ActiveSession) is null;
    }

    void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var paths = State.Account.Paths;
        var path = (sender as MenuItem)?.Tag switch { "Settings" => paths.Settings, "ClaudeMd" => paths.ClaudeMd, "Root" => paths.Root, _ => null };
        if (path is null) return;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Dialog.Show(Main, $"{path}\n\ndoes not exist.", "Nothing to open", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Win32Exception) when (File.Exists(path)) { Process.Start("notepad.exe", $"\"{path}\""); } // no program registered for the type
    }

    async void OnSlash(object sender, RoutedEventArgs e)
    {
        // SubmitAsync checks BlockedReason again: a prompt may have opened since the menu did.
        if (State.ActiveSession is { } session && sender is MenuItem { Tag: string command }) await TuiInput.SubmitAsync(session, command);
    }

    void OnCustomize(object sender, RoutedEventArgs e)
    {
        if (_customize is { IsLoaded: true }) { _customize.Activate(); return; }
        _customize = new Window
        {
            Title = $"Customize · {ShellModel.LetterOf(State.Account.Letter)}", Owner = Main, Content = new CustomizePage(),
            Width = 900, Height = 700, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        _customize.Show();
    }

    async void OnNewSession(object sender, RoutedEventArgs e) => await Main.NewSessionAsync();
}
