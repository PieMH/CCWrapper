using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CCWrapper.Data;

namespace CCWrapper.Views;

public partial class AgentsPanel : UserControl
{
    readonly SessionFollower<SubagentReader, IReadOnlyList<SubagentInfo>> _follower;
    readonly ObservableCollection<AgentRow> _rows = [];
    readonly ICollectionView _view;
    SubagentInfo? _open; // the agent whose log is showing
    string _noAgents = "";

    public AgentsPanel()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = o => AgentRow.Matches(((AgentRow)o).Info, SearchBox.Text);
        // leaving the tab unloads the log and stops its polling: start a fresh one on return
        Loaded += (_, _) => { if (_open is { } o) Detail.Content = new TranscriptView(o.TranscriptPath); };
        _follower = new(this, s => new SubagentReader(s.TranscriptPath!), r => { r.Poll(); return r.Agents; }, Show);
    }

    /// <summary>Null = no session. Updates rows in place so the scroll position survives the polls; the duration moves with the clock.</summary>
    void Show(IReadOnlyList<SubagentInfo>? agents)
    {
        if (agents is null) Close(); // another session: its agents are not this one's
        _noAgents = agents is null ? "No active session" : "No subagents yet";
        var now = DateTimeOffset.Now;
        var rows = (agents ?? []).Select(a => AgentRow.From(a, now)).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            if (i >= _rows.Count) _rows.Add(rows[i]);
            else if (_rows[i] != rows[i]) _rows[i] = rows[i];
        }
        while (_rows.Count > rows.Count) _rows.RemoveAt(_rows.Count - 1);
        UpdateEmpty();
    }

    void UpdateEmpty()
    {
        Empty.Text = _rows.Count == 0 ? _noAgents : "No matching agents";
        Empty.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnSearch(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        UpdateEmpty();
    }

    void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: AgentRow row }) Open(row);
    }

    void OnRowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ListBoxItem { DataContext: AgentRow row }) return;
        Open(row);
        e.Handled = true;
    }

    /// <summary>Swaps the list for the agent's log; the list stays in the tree, so its scroll position and search survive.</summary>
    void Open(AgentRow row)
    {
        _open = row.Info;
        DetailTitle.Text = row.Title;
        Detail.Content = new TranscriptView(row.Info.TranscriptPath);
        (ListPane.Visibility, DetailPane.Visibility) = (Visibility.Collapsed, Visibility.Visible);
        BackButton.Focus(); // the collapsed row held the focus, and Esc needs it in this panel
    }

    void Close()
    {
        if (_open is null) return;
        _open = null;
        Detail.Content = null; // unloading stops the log's polling
        (ListPane.Visibility, DetailPane.Visibility) = (Visibility.Visible, Visibility.Collapsed);
        List.Focus();
    }

    void OnBack(object sender, RoutedEventArgs e) => Close();

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _open is null) return;
        Close();
        e.Handled = true;
    }

    void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.XButton1 || _open is null) return;
        Close();
        e.Handled = true;
    }
}

/// <summary>One row of the Agents list. Record equality lets <see cref="AgentsPanel"/> skip rows that did not change.</summary>
public sealed record AgentRow(SubagentInfo Info, string Age, string? Duration)
{
    public bool IsRunning => Info.IsRunning;
    public string Title => Info.Description ?? Info.AgentType ?? Info.AgentId;
    /// <summary>"Opus 5.5 · 114.1k tokens · high"; what is unknown is left out.</summary>
    public string Subtitle => string.Join(" · ", new[] { BarFormat.ModelLabel(Info.Model),Info.Tokens is { } t ? Tokens(t) + " tokens" : null, Info.Effort }.Where(s => !string.IsNullOrEmpty(s)));
    /// <summary>The hover text: the agent type and the numbers behind the subtitle.</summary>
    public string Tooltip => Detail + "\n" + Stats;
    public string Detail => string.Join(" · ", new[] { Info.AgentType, ShortModel(Info.Model), Info.Effort }.Where(s => !string.IsNullOrEmpty(s)));
    public string Stats => $"ctx {Tokens(Info.ContextTokens)} · out {Tokens(Info.OutputTokens)} · {Info.ToolUseCount} tools · {Age}";

    public static AgentRow From(SubagentInfo info, DateTimeOffset now) =>
        new(info, info.LastActivity is { } t ? FormatAge(now - t) : "—",
            info.FirstActivity is { } first && (info.IsRunning ? now : info.LastActivity) is { } end ? FormatDuration(end - first) : null);

    /// <summary>Case-insensitive match of the trimmed search text in the description or agent type; blank text matches all.</summary>
    public static bool Matches(SubagentInfo info, string query)
    {
        var q = query.Trim();
        return q.Length == 0 || (info.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (info.AgentType?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public static string Tokens(long? n) => n switch
    {
        null => "—",
        < 1000 => n.ToString()!,
        < 1_000_000 => (n.Value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => (n.Value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
    };

    public static string FormatAge(TimeSpan age) => age.TotalSeconds switch
    {
        < 60 => $"{Math.Max(0, (int)age.TotalSeconds)}s",
        < 3600 => $"{(int)age.TotalMinutes}m",
        < 86400 => $"{(int)age.TotalHours}h",
        _ => $"{(int)age.TotalDays}d",
    };

    /// <summary>"42s", "3m 05s", "2h 07m".</summary>
    public static string FormatDuration(TimeSpan d) => d.TotalSeconds switch
    {
        < 60 => $"{Math.Max(0, (int)d.TotalSeconds)}s",
        < 3600 => $"{(int)d.TotalMinutes}m {d.Seconds:00}s",
        _ => $"{(int)d.TotalHours}h {d.Minutes:00}m",
    };

    /// <summary>"claude-opus-4-5-20251101" -> "opus-4-5".</summary>
    public static string? ShortModel(string? model) => model is null ? null : Regex.Replace(model, @"^claude-|-\d{8}$", "");

}
