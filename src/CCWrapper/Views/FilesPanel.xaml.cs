using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CCWrapper.Core;
using CCWrapper.Data;

namespace CCWrapper.Views;

public partial class FilesPanel : UserControl
{
    readonly SessionFollower<FileChanges, IReadOnlyList<FileChange>> _follower;
    IReadOnlyList<FileChange> _shown = [];
    List<FileNode> _roots = [];

    public FilesPanel()
    {
        InitializeComponent();
        _follower = new(this, s => new FileChanges(s.Account.Paths, s.TranscriptPath!, s.Cwd!), r => r.Refresh(), Show);
    }

    /// <summary>Null = no session. Rebuilds the tree only when the list changed, so expansion survives the 2 s polls.</summary>
    void Show(IReadOnlyList<FileChange>? changes)
    {
        Empty.Text = changes is null ? "No active session" : "No files changed yet";
        changes ??= [];
        Empty.Visibility = changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (changes.SequenceEqual(_shown)) return;
        var collapsed = FileTree.Walk(_roots).Where(n => n.IsFolder && !n.IsExpanded).Select(n => n.Key).ToHashSet();
        _shown = changes;
        Tree.ItemsSource = _roots = FileTree.Build(changes, collapsed);
    }

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
            if (Path.IsPathRooted(c.RelPath)) list = Folder(outside.Children, dir, OutsideName + "\\" + dir).Children;
            else
            {
                list = roots;
                var key = "";
                foreach (var part in dir.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
                    list = Folder(list, part, key += part + "\\").Children;
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

        FileNode Folder(List<FileNode> siblings, string name, string key)
        {
            var node = siblings.Find(n => n.IsFolder && n.Key == key);
            if (node is null) siblings.Add(node = new FileNode(name, key));
            return node;
        }
    }

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
