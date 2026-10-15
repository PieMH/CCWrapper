using System.Windows;
using CCWrapper.Data;

namespace CCWrapper.Views;

/// <summary>One window, reused for every file: the whole current file, inline, changed lines highlighted.</summary>
public partial class FileDiffWindow : Window
{
    static FileDiffWindow? _instance;
    FileChange? _shown;

    FileDiffWindow() => InitializeComponent();

    public static void ShowFor(FileChange change, Window? owner)
    {
        if (_instance is null)
        {
            _instance = new FileDiffWindow { Owner = owner };
            _instance.Closed += (_, _) => _instance = null;
        }
        _ = _instance.LoadAsync(change);
        _instance.Show();
        _instance.Activate();
    }

    async Task LoadAsync(FileChange c)
    {
        _shown = c;
        Header.Text = $"{c.FullPath}    {FileTree.Summary(c)}";
        Title = "Diff · " + System.IO.Path.GetFileName(c.FullPath);
        if (c.IsBinary)
        {
            Diff.Visibility = Visibility.Collapsed;
            Note.Visibility = Visibility.Visible;
            Note.Text = $"Binary file: {Size(c.BaseSize)} → {Size(c.CurrentSize)}";
            return;
        }
        var (before, after) = await Task.Run(() => (c.BaseText(), c.CurrentText()));
        if (_shown != c) return; // another file was picked meanwhile
        Diff.Visibility = Visibility.Visible;
        Note.Visibility = before is null ? Visibility.Visible : Visibility.Collapsed;
        Note.Text = "Original backup is gone: nothing is highlighted.";
        Diff.SetText(before ?? after ?? "", after ?? "");
    }

    static string Size(long? bytes) => bytes is { } b ? FileTree.Size(b) : "?";
}
