using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CCWrapper.Core;
using CCWrapper.Data;

namespace CCWrapper.Views;

public partial class FilesPanel : UserControl
{
    readonly SessionFollower<FileChanges, IReadOnlyList<FileChange>> _follower;
    IReadOnlyList<FileChange> _shown = [];
    List<FileNode> _roots = [];
    string _none = "";
    // folders the user collapsed in the full tree; kept while a search shows its own, fully expanded tree
    HashSet<string> _collapsed = [];
    bool _filtered;

    public FilesPanel()
    {
        InitializeComponent();
        _follower = new(this, s => new FileChanges(s.Account.Paths, s.TranscriptPath!, s.Cwd!), r => r.Refresh(), Show);
    }

    /// <summary>Null = no session. Rebuilds the tree only when the list changed, so expansion survives the 2 s polls.</summary>
    void Show(IReadOnlyList<FileChange>? changes)
    {
        _none = changes is null ? "No active session" : "No files changed yet";
        changes ??= [];
        if (changes.SequenceEqual(_shown)) { ShowEmpty(); return; }
        _shown = changes;
        Rebuild();
    }

    void OnSearch(object sender, TextChangedEventArgs e) => Rebuild();

    void Rebuild()
    {
        var query = SearchBox.Text.Trim();
        if (!_filtered) _collapsed = FileTree.Walk(_roots).Where(n => n.IsFolder && !n.IsExpanded).Select(n => n.Key).ToHashSet();
        Tree.ItemsSource = _roots = FileTree.Build(FileTree.Filter(_shown, query), query.Length == 0 ? _collapsed : null);
        _filtered = query.Length > 0;
        ShowEmpty();
    }

    void ShowEmpty()
    {
        Empty.Text = _shown.Count > 0 ? "No files match" : _none;
        Empty.Visibility = _roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Right-click (or the menu key) acts on the row it hit: select it, then show the entries that fit it.</summary>
    void OnMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d is not null and not TreeViewItem) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is not TreeViewItem { DataContext: FileNode { Location: { } path, IsFolder: var folder } } item) { e.Handled = true; return; }
        item.IsSelected = true;
        OpenWithItem.Visibility = folder ? Visibility.Collapsed : Visibility.Visible;
        ExplorerItem.Header = folder ? "Open in File Explorer" : "Show in File Explorer";
        // a deleted file is gone from disk, and Explorer would only open its folder
        OpenWithItem.IsEnabled = ExplorerItem.IsEnabled = folder ? Directory.Exists(path) : File.Exists(path);
    }

    void OnOpenWith(object sender, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is not FileNode { Location: { } path }) return;
        var info = new OpenAsInfo { File = path, Flags = OaifAllowRegistration | OaifExec };
        SHOpenWithDialog(Window.GetWindow(this) is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero, ref info); // cancel is an error code too: nothing to report
    }

    void OnExplorer(object sender, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is not FileNode { Location: { } path } node) return;
        var psi = new ProcessStartInfo("explorer.exe");
        foreach (var arg in FileTree.ExplorerArgs(path, node.IsFolder)) psi.ArgumentList.Add(arg);
        try { Process.Start(psi)?.Dispose(); }
        catch (Win32Exception) { } // no explorer.exe: nothing sensible to show
    }

    const uint OaifAllowRegistration = 1, OaifExec = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OpenAsInfo { public string File; public string? Class; public uint Flags; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHOpenWithDialog(IntPtr parent, ref OpenAsInfo info);

    void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // raised once per TreeViewItem up the chain; only the one that was clicked (and so selected) acts
        if (sender is TreeViewItem { IsSelected: true, DataContext: FileNode { Change: { } c } })
            FileDiffWindow.ShowFor(c, Window.GetWindow(this));
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Tree.SelectedItem is FileNode { Change: { } c })
            FileDiffWindow.ShowFor(c, Window.GetWindow(this));
    }
}

/// <summary>A folder or a changed file in the Files tree.</summary>
public sealed class FileNode(string name, string key, FileChange? change = null)
{
    public string Name { get; } = name;
    /// <summary>Identifies a folder across rebuilds, to keep it collapsed.</summary>
    public string Key { get; } = key;
    public FileChange? Change { get; } = change;
    /// <summary>The path on disk; null for the "outside project" node, which stands for no folder.</summary>
    public string? Location { get; init; } = change?.FullPath;
    public bool IsFolder => Change is null;
    public string Tip => Change?.FullPath ?? Name;
    public bool IsExpanded { get; set; } = true;
    public List<FileNode> Children { get; } = [];
    public string Summary => Change is null ? "" : FileTree.Summary(Change);
    public string Badge => Change?.Kind == ChangeKind.Added ? "A" : "";
    /// <summary>A text file's "+12" and "−3", shown in green and red; <see cref="Other"/> is the rest of <see cref="Summary"/>
    /// (a binary file's size change, or "?").</summary>
    public string Plus => Change is { IsBinary: false, Added: { } a, Removed: not null } ? $"+{a}" : "";
    public string Minus => Change is { IsBinary: false, Added: not null, Removed: { } r } ? $"−{r}" : "";
    public string Other => Plus.Length > 0 ? "" : Summary;
}

public static class FileTree
{
    public const string OutsideName = "outside project";

    /// <summary>Folders by RelPath segment, files in them; folders first, then by name. Files outside the cwd (rooted RelPath)
    /// go under an "outside project" node, one child per directory. Folders named in <paramref name="collapsed"/> start collapsed.</summary>
    public static List<FileNode> Build(IEnumerable<FileChange> changes, ISet<string>? collapsed = null)
    {
        var roots = new List<FileNode>();
        var outside = new FileNode(OutsideName, OutsideName);
        foreach (var c in changes)
        {
            var dir = Path.GetDirectoryName(c.RelPath) ?? "";
            List<FileNode> list;
            if (Path.IsPathRooted(c.RelPath)) list = Folder(outside.Children, dir, OutsideName + "\\" + dir, dir).Children;
            else
            {
                list = roots;
                var key = "";
                var parts = dir.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < parts.Length; i++)
                {
                    var folder = Path.GetDirectoryName(c.FullPath); // each folder is this many levels above the file's own
                    for (var up = parts.Length - 1 - i; up > 0 && folder is not null; up--) folder = Path.GetDirectoryName(folder);
                    list = Folder(list, parts[i], key += parts[i] + "\\", folder).Children;
                }
            }
            list.Add(new FileNode(Path.GetFileName(c.RelPath), c.RelPath, c));
        }
        foreach (var n in Walk(roots.Append(outside)).Where(n => n.IsFolder))
        {
            n.Children.Sort(ByKindThenName);
            n.IsExpanded = collapsed?.Contains(n.Key) != true;
        }
        roots.Sort(ByKindThenName);
        if (outside.Children.Count > 0) roots.Add(outside); // always last
        return roots;

        FileNode Folder(List<FileNode> siblings, string name, string key, string? location)
        {
            var node = siblings.Find(n => n.IsFolder && n.Key == key);
            if (node is null) siblings.Add(node = new FileNode(name, key) { Location = location });
            return node;
        }
    }

    /// <summary>The changes whose relative path (so also the file name) contains <paramref name="query"/>, ignoring case; all of them for an empty query.</summary>
    public static IEnumerable<FileChange> Filter(IEnumerable<FileChange> changes, string query) =>
        query.Length == 0 ? changes : changes.Where(c => c.RelPath.Contains(query, StringComparison.OrdinalIgnoreCase));

    /// <summary>Explorer's arguments: a file is selected in its folder, a folder is opened. An argument list, never a command string.</summary>
    public static string[] ExplorerArgs(string path, bool folder) => folder ? [path] : ["/select,", path];

    static int ByKindThenName(FileNode a, FileNode b) =>
        a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<FileNode> Walk(IEnumerable<FileNode> nodes) =>
        nodes.SelectMany(n => Walk(n.Children).Prepend(n));

    /// <summary>"+12 −3" for text, "+1.2 KB" for binary, "?" when the base backup is gone.</summary>
    public static string Summary(FileChange c) =>
        c.IsBinary ? (c.BaseSize is { } b && c.CurrentSize is { } n ? (n >= b ? "+" : "−") + Size(Math.Abs(n - b)) : "?")
        : c.Added is { } a && c.Removed is { } r ? $"+{a} −{r}" : "?";

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
    };
}

/// <summary>Follows <see cref="AppState.ActiveSession"/>: while the view is loaded, every 2 s and on session change it runs
/// <c>read</c> on the reader off the UI thread, then <c>show</c> on the UI thread (null = no session).
/// The reader is rebuilt when the session's transcript or cwd changes; a result for an old reader is dropped.</summary>
internal sealed class SessionFollower<TReader, TResult> where TReader : class where TResult : class
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly Func<LiveSession, TReader> _create;
    readonly Func<TReader, TResult> _read;
    readonly Action<TResult?> _show;
    string? _key;
    TReader? _reader;
    bool _busy;

    public SessionFollower(FrameworkElement view, Func<LiveSession, TReader> create, Func<TReader, TResult> read, Action<TResult?> show)
    {
        (_create, _read, _show) = (create, read, show);
        void OnState(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppState.ActiveSession)) view.Dispatcher.InvokeAsync(RefreshAsync);
        }
        _timer.Tick += async (_, _) => await RefreshAsync();
        view.Loaded += (_, _) => { AppState.Current.PropertyChanged += OnState; _timer.Start(); _ = RefreshAsync(); };
        view.Unloaded += (_, _) => { AppState.Current.PropertyChanged -= OnState; _timer.Stop(); };
    }

    async Task RefreshAsync()
    {
        var s = AppState.Current.ActiveSession;
        var key = s is { TranscriptPath: { } t, Cwd: { } c } ? t + "|" + c : null;
        if (key != _key)
        {
            _key = key;
            _reader = key is null ? null : _create(s!);
            _show(null);
        }
        if (_busy || _reader is not { } reader) return;
        _busy = true;
        try
        {
            var result = await Task.Run(() => _read(reader));
            if (reader == _reader) _show(result);
        }
        catch (Exception)
        {
            // the readers swallow IO errors; anything else must not take the app down from a timer: retry next tick
        }
        finally { _busy = false; }
        if (reader != _reader) _ = RefreshAsync(); // session changed while reading
    }
}
