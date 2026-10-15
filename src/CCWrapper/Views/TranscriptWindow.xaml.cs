using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CCWrapper.Core;
using CCWrapper.Data;

namespace CCWrapper.Views;

public partial class TranscriptWindow : Window
{
    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.Register(nameof(Query), typeof(string), typeof(TranscriptWindow), new PropertyMetadata(""));

    /// <summary>The search text; the rows bind to it to highlight their matches.</summary>
    public string Query { get => (string)GetValue(QueryProperty); set => SetValue(QueryProperty, value); }

    ObservableCollection<MessageRow> _rows = [];
    readonly CancellationTokenSource _cts = new();
    List<Hit> _hits = [];
    int _cur; // index into _hits
    MessageRow? _marked; // the row carrying the current-match marker
    ScrollViewer? _scroll;

    /// <summary>Live read-only transcript, matches of <paramref name="query"/> highlighted.
    /// Constructed by MainWindow's IAppCommands.ShowTranscript; keep this signature.</summary>
    public TranscriptWindow(string transcriptPath, string title, string? query = null)
    {
        InitializeComponent();
        Title = title;
        List.ItemsSource = _rows;
        List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) => _scroll = e.OriginalSource as ScrollViewer));
        SearchBox.Text = query ?? "";
        Closed += (_, _) => _cts.Cancel();
        _ = FollowAsync(transcriptPath, _cts.Token);
    }

    /// <summary>Polls the transcript every second off the UI thread and shows what was appended.</summary>
    async Task FollowAsync(string path, CancellationToken ct)
    {
        var reader = new TranscriptReader(path);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                if (await Task.Run(() => reader.Poll() ? reader.Messages.ToArray() : null, ct) is { } messages) Apply(messages);
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { } // window closed
    }

    void Apply(TranscriptMessage[] messages)
    {
        var first = _rows.Count == 0;
        var atBottom = first || _scroll is null || _scroll.VerticalOffset >= _scroll.ScrollableHeight - 2;
        if (first) List.ItemsSource = _rows = new(messages.Select(m => new MessageRow(m)));
        else
            for (var i = 0; i < messages.Length; i++) // a later record can extend an earlier message
            {
                if (i >= _rows.Count) _rows.Add(new MessageRow(messages[i]));
                else if (!ReferenceEquals(_rows[i].Message, messages[i])) _rows[i] = new MessageRow(messages[i]);
            }
        Empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _hits = TranscriptSearch.Find(_rows, Query);
        if (first && _hits.Count > 0) GoTo(0);
        else
        {
            Mark();
            if (atBottom && _rows.Count > 0) ScrollToEnd();
        }
    }

    void ScrollToEnd()
    {
        List.ScrollIntoView(_rows[^1]);
        Dispatcher.InvokeAsync(() => _scroll?.ScrollToEnd(), DispatcherPriority.Background);
    }

    void GoTo(int hit)
    {
        if (_hits.Count == 0) { _cur = 0; Mark(); return; }
        _cur = (hit % _hits.Count + _hits.Count) % _hits.Count;
        Mark();
        var row = _rows[_hits[_cur].Row];
        List.ScrollIntoView(row);
        List.UpdateLayout(); // realizes the row's container
        if (List.ItemContainerGenerator.ContainerFromItem(row) is { } container)
            foreach (var block in Descendants<MatchTextBlock>(container).Where(b => b.Current >= 0)) block.ShowCurrent();
    }

    /// <summary>Puts the current-match marker on its row and updates the "3/12" label.</summary>
    void Mark()
    {
        if (_marked is { } old) old.CurText = old.CurTools = -1;
        _marked = null;
        if (_hits.Count > 0)
        {
            _cur = Math.Min(_cur, _hits.Count - 1);
            var hit = _hits[_cur];
            _marked = _rows[hit.Row];
            (_marked.CurText, _marked.CurTools) = hit.InTools ? (-1, hit.Start) : (hit.Start, -1);
        }
        CountLabel.Text = Query.Length == 0 ? "" : $"{(_hits.Count == 0 ? 0 : _cur + 1)}/{_hits.Count}";
    }

    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        Query = SearchBox.Text;
        _hits = TranscriptSearch.Find(_rows, Query);
        GoTo(0);
    }

    void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        GoTo(_cur + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1));
        e.Handled = true;
    }

    void OnPrev(object sender, RoutedEventArgs e) => GoTo(_cur - 1);
    void OnNext(object sender, RoutedEventArgs e) => GoTo(_cur + 1);

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }
}

/// <summary>A match of the search: the row, whether it is in the tool lines, and its offset in that text.</summary>
public sealed record Hit(int Row, bool InTools, int Start);

public static class TranscriptSearch
{
    /// <summary>Start offsets of the non-overlapping, case-insensitive matches of <paramref name="query"/>.</summary>
    public static IEnumerable<int> Offsets(string text, string query)
    {
        if (query.Length == 0) yield break;
        for (var i = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = text.IndexOf(query, i + query.Length, StringComparison.OrdinalIgnoreCase))
            yield return i;
    }

    /// <summary>All matches in row order: a row's text first, then its tool lines.</summary>
    public static List<Hit> Find(IEnumerable<MessageRow> rows, string query)
    {
        var hits = new List<Hit>();
        var n = 0;
        foreach (var row in rows)
        {
            hits.AddRange(Offsets(row.Text, query).Select(o => new Hit(n, false, o)));
            hits.AddRange(Offsets(row.Tools, query).Select(o => new Hit(n, true, o)));
            n++;
        }
        return hits;
    }
}

/// <summary>One message in the list. <see cref="CurText"/>/<see cref="CurTools"/> are the offset of the current match in that text, or -1.</summary>
public sealed class MessageRow(TranscriptMessage message) : Observable
{
    public TranscriptMessage Message { get; } = message;
    public string Role => Message.Role;
    public string Text => Message.Text;
    public string Tools { get; } = string.Join("\n", message.ToolUses);
    public string When => Message.Time is { } t ? t.ToLocalTime().ToString(t.ToLocalTime().Date == DateTime.Today ? "HH:mm:ss" : "dd MMM HH:mm") : "";
    public int CurText { get; set => Set(ref field, value); } = -1;
    public int CurTools { get; set => Set(ref field, value); } = -1;
}

/// <summary>A TextBlock that paints every <see cref="Query"/> match of <see cref="FullText"/>; the one at offset <see cref="Current"/> stands out.</summary>
public sealed class MatchTextBlock : TextBlock
{
    static readonly Brush MatchBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x5B, 0x00)));
    static readonly Brush CurrentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC2, 0x6A, 0x00)));

    public static readonly DependencyProperty FullTextProperty = Register(nameof(FullText), typeof(string), "");
    public static readonly DependencyProperty QueryProperty = Register(nameof(Query), typeof(string), "");
    public static readonly DependencyProperty CurrentProperty = Register(nameof(Current), typeof(int), -1);

    public string FullText { get => (string)GetValue(FullTextProperty); set => SetValue(FullTextProperty, value); }
    public string Query { get => (string)GetValue(QueryProperty); set => SetValue(QueryProperty, value); }
    public int Current { get => (int)GetValue(CurrentProperty); set => SetValue(CurrentProperty, value); }

    Run? _current;

    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    static DependencyProperty Register(string name, Type type, object defaultValue) =>
        DependencyProperty.Register(name, type, typeof(MatchTextBlock), new PropertyMetadata(defaultValue, (d, _) => ((MatchTextBlock)d).Rebuild()));

    void Rebuild()
    {
        Inlines.Clear();
        _current = null;
        var (text, pos) = (FullText ?? "", 0);
        foreach (var start in TranscriptSearch.Offsets(text, Query ?? ""))
        {
            Inlines.Add(new Run(text[pos..start]));
            var run = new Run(text.Substring(start, Query!.Length)) { Background = start == Current ? CurrentBrush : MatchBrush };
            if (start == Current) _current = run;
            Inlines.Add(run);
            pos = start + Query.Length;
        }
        Inlines.Add(new Run(text[pos..]));
    }

    /// <summary>Scrolls the enclosing ScrollViewer so the current match is visible, even inside a very long message.</summary>
    public void ShowCurrent() => _current?.BringIntoView();
}
