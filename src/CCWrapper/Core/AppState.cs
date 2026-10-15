using System.Collections.ObjectModel;

namespace CCWrapper.Core;

/// <summary>Calls that cross task boundaries; MainWindow implements them. Call on the UI thread.</summary>
public interface IAppCommands
{
    /// <summary>Read-only transcript of an open session, matches of <paramref name="query"/> highlighted.</summary>
    void ShowTranscript(LiveSession session, string? query = null);

    /// <summary>Read-only transcript by path, e.g. a session running in another window or terminal.</summary>
    void ShowTranscript(string transcriptPath, string title, string? query = null);

    /// <summary>Opens the url in the right panel's Browser tab and brings that tab forward.</summary>
    void OpenInBrowser(string url);

    /// <summary>Flashes the taskbar because a background session started waiting for input.</summary>
    void FlashAttention(LiveSession session);
}

/// <summary>State of this window. One account per process, so one instance: MainWindow sets
/// <see cref="Current"/> before building its child views. Null while only the account picker is shown.</summary>
public sealed class AppState(Account account, IAppCommands commands) : Observable
{
    public static AppState Current { get; set; } = null!;

    public Account Account { get; } = account;
    public IAppCommands Commands { get; } = commands;
    public ObservableCollection<LiveSession> OpenSessions { get; } = [];
    public LiveSession? ActiveSession { get; set => Set(ref field, value); }
}
