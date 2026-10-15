using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CCWrapper.Core;
using CCWrapper.Data;

namespace CCWrapper.Views;

public partial class AgentsPanel : UserControl
{
    readonly SessionFollower<SubagentReader, IReadOnlyList<SubagentInfo>> _follower;
    readonly ObservableCollection<AgentRow> _rows = [];

    public AgentsPanel()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        _follower = new(this, s => new SubagentReader(s.TranscriptPath!), r => { r.Poll(); return r.Agents; }, Show);
    }

    /// <summary>Null = no session. Updates rows in place so the scroll position survives the polls; the age text moves with the clock.</summary>
    void Show(IReadOnlyList<SubagentInfo>? agents)
    {
        Empty.Text = agents is null ? "No active session" : "No subagents yet";
        var now = DateTimeOffset.Now;
        var rows = (agents ?? []).Select(a => AgentRow.From(a, now)).ToList();
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < rows.Count; i++)
        {
            if (i >= _rows.Count) _rows.Add(rows[i]);
            else if (_rows[i] != rows[i]) _rows[i] = rows[i];
        }
        while (_rows.Count > rows.Count) _rows.RemoveAt(_rows.Count - 1);
    }

    void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: AgentRow row })
            AppState.Current.Commands.ShowTranscript(row.Info.TranscriptPath, row.Title);
    }
}

/// <summary>One line group of the Agents list. Record equality lets <see cref="AgentsPanel"/> skip rows that did not change.</summary>
public sealed record AgentRow(SubagentInfo Info, string Age)
{
    public bool IsRunning => Info.IsRunning;
    public string Title => Info.Description ?? Info.AgentType ?? Info.AgentId;
    public string Detail => string.Join(" · ", new[] { Info.AgentType, ShortModel(Info.Model), Info.Effort }.Where(s => !string.IsNullOrEmpty(s)));
    public string Stats => $"ctx {Tokens(Info.ContextTokens)} · out {Tokens(Info.OutputTokens)} · {Info.ToolUseCount} tools · {Age}";

    public static AgentRow From(SubagentInfo info, DateTimeOffset now) => new(info, info.LastActivity is { } t ? FormatAge(now - t) : "—");

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

    /// <summary>"claude-opus-4-5-20251101" -> "opus-4-5".</summary>
    public static string? ShortModel(string? model) => model is null ? null : Regex.Replace(model, @"^claude-|-\d{8}$", "");
}
