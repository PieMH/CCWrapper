using System.Windows;
using CCWrapper.Core;

namespace CCWrapper.Views;

public partial class MainWindow : Window, IAppCommands
{
    public MainWindow(Account account)
    {
        AppState.Current = new AppState(account, this); // before InitializeComponent: child views read it
        InitializeComponent();
        var letter = account.Letter.ToUpperInvariant();
        Title = $"CCWrapper · {letter} · {account.Label}";
        AccountLabel.Text = $"{letter} · {account.Label}";
    }

    public void ShowTranscript(LiveSession session, string? query = null)
    {
        if (session.TranscriptPath is { } path)
            ShowTranscript(path, session.Title ?? session.SessionId ?? "Transcript", query);
    }

    public void ShowTranscript(string transcriptPath, string title, string? query = null) =>
        new TranscriptWindow(transcriptPath, title, query) { Owner = this }.Show();

    public void OpenInBrowser(string url)
    {
        RightTabs.SelectedItem = BrowserTab;
        Browser.Navigate(url);
    }

    public void FlashAttention(LiveSession session) { } // TODO(T5): taskbar flash
}
