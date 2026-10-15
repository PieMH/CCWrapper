using System.Windows;

namespace CCWrapper.Views;

public partial class TranscriptWindow : Window
{
    /// <summary>Live read-only transcript, matches of <paramref name="query"/> highlighted.
    /// Constructed by MainWindow's IAppCommands.ShowTranscript; keep this signature.</summary>
    public TranscriptWindow(string transcriptPath, string title, string? query = null)
    {
        InitializeComponent();
        Title = title; // TODO(T7): render transcriptPath, highlight query
    }
}
