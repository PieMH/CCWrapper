using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;

namespace CCWrapper.Views;

public partial class ComposeBox : UserControl
{
    readonly Dictionary<Guid, PromptHistory> _history = [];
    LiveSession? _session;
    string? _refusal; // why the last submit to this session (from any view) did not go in; cleared by one that does
    bool _sending;

    public ComposeBox()
    {
        InitializeComponent();
        _ = new SessionWatch(this, Refresh);
        Loaded += (_, _) => { TuiInput.Changed -= OnTuiInput; TuiInput.Changed += OnTuiInput; };
        Unloaded += (_, _) => TuiInput.Changed -= OnTuiInput;
    }

    void OnTuiInput(LiveSession s, string? refusal)
    {
        if (s != _session) return;
        _refusal = refusal;
        Refresh(s);
    }

    // The box stays editable while blocked: only sending waits.
    void Refresh(LiveSession? session)
    {
        if (session != _session) _refusal = null;
        _session = session;
        var blocked = TuiInput.BlockedReason(session);
        Notice.Text = blocked ?? _refusal;
        Notice.Visibility = Notice.Text is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = blocked is null && !_sending;
        Input.IsReadOnly = _sending; // it is cleared once the text went in
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
            if (await TuiInput.SubmitAsync(session, text) is not null) return; // keep the text: not submitted, the notice says why
            HistoryOf(session).Add(text);
            Input.Clear();
        }
        finally
        {
            _sending = false;
            Refresh(_session);
            Input.Focus();
        }
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.Enter && (mods & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            if (SendButton.IsEnabled) _ = SendAsync();
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
    public const string ClearInputFirst = "Clear the terminal input first: the command would be added to the text there";
    public const string BashModeFirst = "Clear the terminal input first: it is in bash mode (!) and the text would run as a shell command";
    const string StatusUnreadable = "Could not read Claude Code's status — try again";
    const int ScreenRows = 12;

    static readonly HashSet<LiveSession> Switching = []; // UI thread only

    /// <summary>On the UI thread: a mode switch of the session started or ended (null), or a submit to it was refused
    /// (the reason) or went in (null).</summary>
    public static event Action<LiveSession, string?>? Changed;

    /// <summary>Null when typing into the session works; else the hint to show instead.
    /// Registry "status":"waiting" means a dialog, permission prompt, question or similar is open: its "waitingFor" is
    /// "dialog open", "permission prompt", "input needed", "sandbox request", "goal proposal"... so any Waiting blocks.</summary>
    public static string? BlockedReason(LiveSession? s) => s is not null && Switching.Contains(s) ? "Switching mode…" : Blocked(s);

    static string? Blocked(LiveSession? s) =>
        s is null ? "No active session"
        : s.Status == SessionStatus.Exited ? "This session has ended"
        : s.Status == SessionStatus.Waiting ? WaitingText(s.WaitingFor)
        : s.Channel is null ? "Starting…"
        : null;

    static string WaitingText(string? waitingFor) =>
        $"Claude is waiting ({(string.IsNullOrEmpty(waitingFor) ? "dialog open" : waitingFor)}): answer it in the terminal";

    /// <summary>The bottom bar's Shift+Tab loop runs: <see cref="BlockedReason"/> says so until it ends.</summary>
    public static void SetSwitching(LiveSession s, bool on)
    {
        if (on ? Switching.Add(s) : Switching.Remove(s)) Changed?.Invoke(s, null);
    }

    /// <summary><see cref="BlockedReason"/> with the session's registry entry (by pid, else session id) read now rather than
    /// as of the last poll: a prompt that opened since then would take the next key. The tab's own pid must have an entry
    /// (its file may be torn mid-write: read again a few times), else it is blocked. The mode switch itself passes
    /// <paramref name="switching"/>.</summary>
    public static async Task<string?> FreshBlockedReasonAsync(LiveSession s, bool switching = false)
    {
        if ((switching ? Blocked(s) : BlockedReason(s)) is { } why) return why;
        try
        {
            var running = await Task.Run(() =>
            {
                for (var tries = 1; ; tries++)
                {
                    var r = SessionCatalog.Running(s.Account); // reads each file once: a torn one is silently missing
                    if (s.Pid is not { } pid || r.Any(x => x.Pid == pid) || tries == 4) return r;
                    Thread.Sleep(50);
                }
            });
            if (s.Pid is { } pid && !running.Any(r => r.Pid == pid)) return StatusUnreadable; // fail closed
            return ShellModel.MapStatus([s], running).Select(m => m.Registry).FirstOrDefault() is { Status: SessionStatus.Waiting } r
                ? WaitingText(r.WaitingFor) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusUnreadable; // fail closed
        }
    }

    /// <summary>One bracketed paste, then Enter after <see cref="EnterDelay"/> (an Enter sent with the paste would be taken as
    /// part of it). The registry is read again right before each. Any text is refused while the TUI's input is in bash mode
    /// (it would run as a shell command), and a slash command while it holds text. Null when it went in, else why not.</summary>
    public static async Task<string?> SubmitAsync(LiveSession? s, string text)
    {
        var refusal = await TrySubmitAsync(s, text);
        if (s is not null) Changed?.Invoke(s, refusal);
        return refusal;
    }

    static async Task<string?> TrySubmitAsync(LiveSession? s, string text)
    {
        if (s is null || BlockedReason(s) is not null) return BlockedReason(s);
        if (s.Channel is IScreenChannel screen)
        {
            var tail = await screen.ReadTailAsync(ScreenRows);
            if (InBashMode(tail)) return BashModeFirst;
            if (text.StartsWith('/') && HasPromptText(tail)) return ClearInputFirst;
        }
        if (await FreshBlockedReasonAsync(s) is { } before) return before;
        if (!s.SendText(text)) return BlockedReason(s) ?? "Not sent";
        await Task.Delay(EnterDelay(text.Length));
        if (await FreshBlockedReasonAsync(s) is { } after) return after + ". The text is in the terminal input, not sent.";
        return s.SendKeys("\r") ? null : BlockedReason(s) ?? "Not sent";
    }

    /// <summary>A long paste takes the TUI longer to take in: 80 ms + 1 ms per 100 characters, at most 1 s.</summary>
    public static TimeSpan EnterDelay(int length) => TimeSpan.FromMilliseconds(Math.Min(1000, 80 + length / 100));

    /// <summary>True when the TUI's input holds typed text: its prompt row, a "❯ ", "> " or bash-mode "! " row, has typed text or
    /// continuation rows. See <see cref="PromptOf"/>.
    /// shortcut: the placeholder is told apart only by its 'Try "' start (colors are not read); revisit if Claude Code changes it.</summary>
    public static bool HasPromptText(string? screen) => PromptOf(screen) is { Text: true };

    /// <summary>True when the TUI's prompt row starts with the bash-mode "!", typed text or not.</summary>
    public static bool InBashMode(string? screen) => PromptOf(screen) is { Marker: '!' };

    static readonly Regex Rule = new(@"^\s*[╭┌╰└]?─{3,}"), PromptRow = new(@"^\s*│?\s*([❯>!])(?:\s+(.*?))?\s*│?\s*$");

    /// <summary>The last prompt row that has a ─ rule (or box border) right above it and another below it (after the
    /// continuation rows of a multi-line input), as Claude Code frames its input; so echoed prompts in the transcript and
    /// a status line under the box do not count. Null when none.</summary>
    static (char Marker, bool Text)? PromptOf(string? screen)
    {
        var rows = (screen ?? "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        for (var i = rows.Count - 2; i > 0; i--)
        {
            if (!Rule.IsMatch(rows[i - 1]) || PromptRow.Match(rows[i]) is not { Success: true } m) continue;
            var continued = rows.Skip(i + 1).TakeWhile(r => !Rule.IsMatch(r)).Count();
            if (i + 1 + continued == rows.Count) continue; // no rule below
            return (m.Groups[1].Value[0], continued > 0 || (m.Groups[2].Value is { Length: > 0 } typed && !typed.StartsWith("Try \"")));
        }
        return null;
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
