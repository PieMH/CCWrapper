using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CCWrapper.Core;
using CCWrapper.Terminal;

namespace CCWrapper.Views;

public partial class ComposeBox : UserControl
{
    readonly Dictionary<Guid, PromptHistory> _history = [];
    LiveSession? _session;
    bool _sending;

    public ComposeBox()
    {
        InitializeComponent();
        _ = new SessionWatch(this, Refresh);
    }

    void Refresh(LiveSession? session)
    {
        _session = session;
        var reason = TuiInput.BlockedReason(session);
        Notice.Text = reason;
        Notice.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
        Input.IsEnabled = SendButton.IsEnabled = reason is null && !_sending;
    }

    PromptHistory HistoryOf(LiveSession s) => _history.TryGetValue(s.TabId, out var h) ? h : _history[s.TabId] = new();

    void OnSend(object sender, RoutedEventArgs e) => _ = SendAsync();

    async Task SendAsync()
    {
        var session = _session;
        var text = Input.Text.TrimEnd();
        if (session is null || text.Length == 0 || _sending) return;
        _sending = true;
        Refresh(session);
        try
        {
            if (!await TuiInput.SubmitAsync(session, text)) return; // keep the text: nothing went in, or blocked meanwhile
            HistoryOf(session).Add(text);
            Input.Clear();
        }
        finally
        {
            _sending = false;
            Refresh(_session);
            if (Input.IsEnabled) Input.Focus();
        }
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.Enter && (mods & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _ = SendAsync();
        }
        else if ((e.Key == Key.V && mods == ModifierKeys.Control) || (e.Key == Key.Insert && mods == ModifierKeys.Shift))
        {
            e.Handled = true; // PasteHandler also covers files and bitmaps, which TextBox's own paste ignores
            try { if (Clipboard.GetDataObject() is { } data && PasteHandler.ForCompose(data) is { } text) Insert(text); }
            catch (ExternalException) { } // clipboard held by another app
        }
        else if (e.Key == Key.Up && _session is not null
            && Input.GetLineIndexFromCharacterIndex(Input.CaretIndex) == 0 && HistoryOf(_session).Previous(Input.Text) is { } prev)
        {
            e.Handled = true;
            Recall(prev);
        }
        else if (e.Key == Key.Down && _session is not null
            && Input.GetLineIndexFromCharacterIndex(Input.CaretIndex) == Input.LineCount - 1 && HistoryOf(_session).Next(Input.Text) is { } next)
        {
            e.Handled = true;
            Recall(next);
        }
    }

    void Recall(string text)
    {
        Input.Text = text;
        Input.CaretIndex = text.Length;
    }

    void Insert(string text)
    {
        var at = Input.SelectionStart;
        Input.SelectedText = text;
        Input.Select(at + text.Length, 0);
    }

    // Files and bitmaps go through PasteHandler; dropped text is left to the TextBox.
    static bool IsFileOrImage(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop) || PasteHandler.HasImage(data);

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (!IsFileOrImage(e.Data)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (!IsFileOrImage(e.Data)) return;
        e.Handled = true;
        if (PasteHandler.ForCompose(e.Data) is { } text) Insert(text);
        Input.Focus();
    }
}

/// <summary>What may go into the TUI input, and how it goes in. Shared by the compose box, the bottom bar and the Customize page.</summary>
public static class TuiInput
{
    /// <summary>Null when typing into the session works; else the hint to show instead.
    /// Registry "status":"waiting" means a dialog, permission prompt, question or similar is open: its "waitingFor" is
    /// "dialog open", "permission prompt", "input needed", "sandbox request", "goal proposal"... so any Waiting blocks.</summary>
    public static string? BlockedReason(LiveSession? s) =>
        s is null ? "No active session"
        : s.Status == SessionStatus.Exited ? "This session has ended"
        : s.Status == SessionStatus.Waiting
            ? $"Claude is waiting ({(string.IsNullOrEmpty(s.WaitingFor) ? "dialog open" : s.WaitingFor)}): answer it in the terminal"
        : s.Channel is null ? "Starting…"
        : null;

    /// <summary>One bracketed paste, then Enter after a moment (an Enter sent with the paste would be taken as part of it).
    /// False when blocked or nothing was sent.</summary>
    public static async Task<bool> SubmitAsync(LiveSession s, string text)
    {
        if (BlockedReason(s) is not null || !s.SendText(text)) return false;
        await Task.Delay(80);
        return s.SendKeys("\r");
    }
}

/// <summary>Sent prompts of one session, oldest first. Up/Down browse them while the box is empty or shows a recalled one.</summary>
public sealed class PromptHistory
{
    readonly List<string> _items = [];
    int _pos; // == _items.Count: not browsing

    public void Add(string text)
    {
        if (_items.Count == 0 || _items[^1] != text) _items.Add(text);
        _pos = _items.Count;
    }

    /// <summary>The older prompt, or null when <paramref name="box"/> holds something typed or nothing is older.</summary>
    public string? Previous(string box)
    {
        if (box.Length == 0) _pos = _items.Count;
        if (_pos == 0 || (box.Length > 0 && (_pos == _items.Count || box != _items[_pos]))) return null;
        return _items[--_pos];
    }

    /// <summary>The newer prompt, "" past the newest, or null when <paramref name="box"/> is not a recalled prompt.</summary>
    public string? Next(string box)
    {
        if (_pos >= _items.Count || box != _items[_pos]) return null;
        return ++_pos < _items.Count ? _items[_pos] : "";
    }
}

/// <summary>Calls <c>changed</c> on the UI thread with the active session at load, when it is replaced and
/// whenever one of its properties changes (those may fire on other threads) while <c>view</c> is loaded.</summary>
internal sealed class SessionWatch
{
    LiveSession? _session;

    public SessionWatch(FrameworkElement view, Action<LiveSession?> changed)
    {
        void Notify() => view.Dispatcher.InvokeAsync(() => changed(_session));
        void OnSession(object? _, PropertyChangedEventArgs e) => Notify();
        void Attach(LiveSession? s)
        {
            if (_session is not null) _session.PropertyChanged -= OnSession;
            _session = s;
            if (s is not null) s.PropertyChanged += OnSession;
        }
        void OnState(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(AppState.ActiveSession)) return;
            Attach(AppState.Current.ActiveSession);
            Notify();
        }
        view.Loaded += (_, _) =>
        {
            if (AppState.Current is not { } app) return;
            app.PropertyChanged -= OnState;
            app.PropertyChanged += OnState;
            Attach(app.ActiveSession);
            Notify();
        };
        view.Unloaded += (_, _) =>
        {
            if (AppState.Current is { } app) app.PropertyChanged -= OnState;
            Attach(null);
        };
    }
}
