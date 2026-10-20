using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;

namespace CCWrapper.Views;

public partial class ComposeBox : UserControl
{
    const string DefaultHint = "Message Claude. Enter sends, Shift+Enter adds a line, paste or drop files and images… ";

    readonly Dictionary<Guid, PromptHistory> _history = [];
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) }, _blink = new() { Interval = TimeSpan.FromMilliseconds(530) };
    LiveSession? _session;
    string? _refusal; // why the last submit to this session (from any view) did not go in; cleared by one that does
    string? _suggestion; // the prompt Claude Code suggests in the session's empty input, mirrored while this box is empty
    bool _sending, _polling;
    readonly Dictionary<(string, string?), List<SlashCommand>> _commands = []; // per (account, folder), refreshed each time the list is about to open
    bool _forced, _quiet, _fresh, _loading; // _forced: opened from the + menu over text; _quiet: a change made here must not open the list
    string? _query;

    public ComposeBox()
    {
        InitializeComponent();
        _ = new SessionWatch(this, Refresh);
        _poll.Tick += OnPoll;
        _blink.Tick += (_, _) => HintCaret.Foreground = HintCaret.Foreground == Brushes.Transparent ? (Brush)FindResource("Text") : Brushes.Transparent;
        Loaded += (_, _) => { TuiInput.Changed -= OnTuiInput; TuiInput.Changed += OnTuiInput; _poll.Start(); UpdateHint(); };
        Unloaded += (_, _) => { TuiInput.Changed -= OnTuiInput; _poll.Stop(); _blink.Stop(); };
        IsVisibleChanged += (_, _) => { UpdateHint(); CloseSlash(); };
    }

    // Reads the terminal's screen only while the box is empty and shown; a stale read for another session is dropped.
    async void OnPoll(object? sender, EventArgs e)
    {
        if (_polling || Input.Text.Length > 0 || !IsVisible) return;
        _polling = true;
        try
        {
            var session = _session;
            var screen = session?.Channel is IScreenChannel channel ? await channel.ReadTailAsync(TuiInput.ScreenRows, styled: true) : null;
            var suggestion = TuiInput.SuggestionOf(screen);
            if (session != _session || suggestion == _suggestion) return;
            _suggestion = suggestion;
            UpdateHint();
        }
        finally { _polling = false; }
    }

    void OnHintChanged(object sender, RoutedEventArgs e) => UpdateHint();

    void OnTextChanged(object sender, RoutedEventArgs e)
    {
        _forced = false;
        Stash(_session);
        UpdateHint();
        UpdateSlash();
        UpdateColor();
    }

    // The box's text is the active session's draft, kept in the store as it changes (written to disk only on exit).
    void Stash(LiveSession? s)
    {
        if (s is not null) DraftStore.For(s.Account).Set(s, Input.Text, Input.CaretIndex);
    }

    void OnCaretMoved(object sender, RoutedEventArgs e) => UpdateSlash();

    void OnLostFocus(object sender, RoutedEventArgs e)
    {
        UpdateHint();
        CloseSlash();
    }

    // ---- the "/" list ----

    (string, string?)? CommandsKey => _session is { } s ? (s.Account.Letter, s.Cwd) : null;

    // Open while the caret is in a word that starts with "/" (at the text start or after whitespace; not a path) or forced from the + menu: filtered by what follows the "/".
    void UpdateSlash()
    {
        if (_quiet) return;
        if (CommandsKey is not { } key) { CloseSlash(); return; }
        var text = Input.Text;
        string? query = null;
        if (_forced) query = "";
        else if (Input.IsKeyboardFocused && SlashWord.At(text, Input.CaretIndex) is var (start, end)) query = text[(start + 1)..end];
        if (query is null) { CloseSlash(); return; }
        if (!_fresh && !_loading) _ = LoadCommandsAsync(key, _session!);
        var rows = SlashCommands.Filter(_commands.GetValueOrDefault(key) ?? [], query);
        if (rows.Count == 0) { SlashPopup.IsOpen = false; _query = null; return; } // keeps _fresh: no reload per key typed
        if (SlashPopup.IsOpen && query == _query) return;
        _query = query;
        SlashList.ItemsSource = rows;
        SlashList.SelectedIndex = 0;
        SlashPopup.IsOpen = true;
    }

    async Task LoadCommandsAsync((string, string?) key, LiveSession session)
    {
        _loading = true;
        try
        {
            _commands[key] = await Task.Run(() => SlashCommands.List(session.Account, session.Cwd));
            _fresh = key == CommandsKey; // the active session may have changed meanwhile
        }
        finally { _loading = false; }
        _query = null; // the list may have grown under the same query
        UpdateSlash();
        UpdateColor();
    }

    // shortcut: the whole text takes the color while it starts with a known command (a TextBox cannot color a range, and an overlay
    // would lose IME composition and drift from the caret); upgrade if tokens inside the text must be told apart.
    void UpdateColor()
    {
        var text = Input.Text;
        if (CommandsKey is not { } key) { Input.ClearValue(ForegroundProperty); return; }
        if (!_commands.TryGetValue(key, out var list))
        {
            if (text.StartsWith('/') && !_loading) _ = LoadCommandsAsync(key, _session!); // a recalled "/command" never opens the list
            list = [];
        }
        if (SlashWord.StartsWithCommand(text, list)) Input.Foreground = (Brush)FindResource("SlashText");
        else Input.ClearValue(ForegroundProperty);
    }

    void CloseSlash()
    {
        SlashPopup.IsOpen = false;
        _fresh = _forced = false;
        _query = null;
    }

    void MoveSlash(int by)
    {
        SlashList.SelectedIndex = Math.Clamp(SlashList.SelectedIndex + by, 0, SlashList.Items.Count - 1);
        SlashList.ScrollIntoView(SlashList.SelectedItem);
    }

    // Completes to "/name " (the rest of the box stays); sending is the caller's.
    void CompleteSlash(SlashCommand command)
    {
        var insert = $"/{command.Name} ";
        _quiet = true;
        if (_forced) Insert(insert);
        else
        {
            var text = Input.Text;
            if (SlashWord.At(text, Input.CaretIndex) is var (start, end))
                SetText(text[..start] + insert + text[end..].TrimStart(' ', '\t'), start + insert.Length);
            else Insert(insert);
        }
        _quiet = false;
        CloseSlash();
        Input.Focus();
    }

    void OnSlashClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SlashList, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: SlashCommand c })
        {
            e.Handled = true;
            CompleteSlash(c);
        }
    }

    // ---- the + menu ----

    void OnPlus(object sender, RoutedEventArgs e)
    {
        PlusButton.ContextMenu.PlacementTarget = PlusButton;
        PlusButton.ContextMenu.IsOpen = true;
    }

    void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Add files", Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) InsertPaths(dialog.FileNames);
    }

    void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Add folders", Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) InsertPaths(dialog.FolderNames);
    }

    // Inserted as a drop of those files would be.
    void InsertPaths(string[] paths)
    {
        if (PasteHandler.ForCompose(new DataObject(DataFormats.FileDrop, paths)) is { } text) Insert(text);
        Input.Focus();
    }

    // After the menu has closed and given the focus back: an empty box gets the "/", a box with text only gets the full list.
    void OnSlashMenu(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        Input.Focus();
        if (Input.Text.Length == 0) { Input.Text = "/"; Input.CaretIndex = 1; } else _forced = true;
        UpdateSlash();
    });

    // The caret stands where typing would go, as in an editor: shown when the box is empty, unfocused and has no suggestion.
    void UpdateHint()
    {
        HintText.Text = _suggestion ?? DefaultHint;
        var caret = _suggestion is null && Input.Text.Length == 0 && !Input.IsKeyboardFocusWithin && IsVisible;
        HintCaret.Foreground = caret ? (Brush)FindResource("Text") : Brushes.Transparent;
        _blink.IsEnabled = caret && SystemParameters.ClientAreaAnimation;
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
        if (session != _session)
        {
            Stash(_session); // with the caret
            _refusal = null;
            _suggestion = null;
            UpdateHint();
            CloseSlash();
            _session = session;
            var draft = session is null ? null : DraftStore.For(session.Account).Get(session);
            SetText(draft?.Text ?? "", Math.Min(draft?.Caret ?? 0, draft?.Text.Length ?? 0)); // _quiet: a restored "/cmd" does not open the list
        }
        _session = session;
        Stash(session); // a tab that just got its session id moves its draft to it
        UpdateColor();
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
        var text = Input.Text.TrimEnd() is { Length: > 0 } typed ? typed : _suggestion ?? ""; // Enter on an empty box sends the suggestion, as in the TUI
        if (session is null || text.Length == 0 || _sending) return;
        _sending = true;
        Refresh(session);
        try
        {
            if (await TuiInput.SubmitAsync(session, text) is not null) return; // keep the text: not submitted, the notice says why
            HistoryOf(session).Add(text);
            if (session == _session) Input.Clear();
            else DraftStore.For(session.Account).Set(session, "", 0); // the box shows another session's draft by now
            _suggestion = null; // it was sent, or Claude Code dropped it when the text went in
            UpdateHint();
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
        if (SlashPopup.IsOpen && mods == ModifierKeys.None && e.Key is Key.Up or Key.Down or Key.Enter or Key.Tab or Key.Escape)
        {
            e.Handled = true; // before the prompt history and sending
            if (e.Key == Key.Escape) CloseSlash();
            else if (e.Key is Key.Up or Key.Down) MoveSlash(e.Key == Key.Up ? -1 : 1);
            else if (SlashList.SelectedItem is SlashCommand c)
            {
                // Enter runs the chosen command, as in the TUI, when it is the start of the text; Tab, a command put into text and a word
                // in the middle of a prompt (sending would submit it half written) only complete
                var send = e.Key == Key.Enter && !_forced && SlashWord.At(Input.Text, Input.CaretIndex) is { Start: 0 };
                CompleteSlash(c);
                if (send && SendButton.IsEnabled) _ = SendAsync();
            }
        }
        else if (e.Key == Key.Enter && (mods & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            if (SendButton.IsEnabled) _ = SendAsync();
        }
        else if (e.Key == Key.Tab && mods == ModifierKeys.None && Input.Text.Length == 0 && _suggestion is { } suggestion)
        {
            e.Handled = true; // takes the suggestion to edit, as Tab does in the TUI
            Recall(suggestion);
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

    /// <summary>Puts <paramref name="text"/> in an empty box and focuses it. A box with text keeps it: the text is never replaced
    /// or added to, only focused. Not sent.</summary>
    public void Prefill(string text)
    {
        if (Input.Text.Length == 0) Recall(text);
        Input.Focus();
    }

    void Recall(string text) => SetText(text, text.Length);

    // _quiet: recalled text starting with "/" must not open the list (Up/Down would then move in it instead of the history).
    void SetText(string text, int caret)
    {
        var was = _quiet;
        _quiet = true;
        Input.Text = text;
        Input.CaretIndex = caret;
        _quiet = was;
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

/// <summary>Where the compose box's "/" word is.</summary>
public static class SlashWord
{
    static bool Space(char c) => c is ' ' or '\t' or '\r' or '\n';

    /// <summary>The word the caret is in when it starts with "/" at the text start or right after whitespace (so not "a/b" or a URL)
    /// and the caret is after that "/" and not past the word's end: its start (the "/") and end (exclusive). Else null.</summary>
    public static (int Start, int End)? At(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret;
        while (start > 0 && !Space(text[start - 1])) start--;
        if (start >= text.Length || text[start] != '/' || caret <= start) return null;
        var end = start;
        while (end < text.Length && !Space(text[end])) end++;
        return (start, end);
    }

    /// <summary>True when the text starts with "/name", the name being one of <paramref name="commands"/> (case-insensitive).</summary>
    public static bool StartsWithCommand(string text, IEnumerable<SlashCommand> commands)
    {
        if (!text.StartsWith('/')) return false;
        var end = 1;
        while (end < text.Length && !Space(text[end])) end++;
        var name = text[1..end];
        return name.Length > 0 && commands.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>What may go into the TUI input, and how it goes in. Shared by the compose box, the bottom bar and the
/// reload button of Plugins &amp; connectors.</summary>
public static class TuiInput
{
    public const string ClearInputFirst = "Clear the terminal input first: the command would be added to the text there";
    public const string BashModeFirst = "Clear the terminal input first: it is in bash mode (!) and the text would run as a shell command";
    const string StatusUnreadable = "Could not read Claude Code's status — try again";
    internal const int ScreenRows = 200; // the whole screen (ccw.tail stops at its top): a tall multi-line input must keep its top rule in view

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
            ShellModel.Rebind([s], running, ProcessTree.Parents);
            if (s.Pid is { } pid && !running.Any(r => r.Pid == pid)) return StatusUnreadable; // fail closed
            return ShellModel.MapStatus([s], running).Select(m => m.Registry).FirstOrDefault() is { Status: SessionStatus.Waiting } r
                ? WaitingText(r.WaitingFor) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusUnreadable; // fail closed
        }
    }

    /// <summary>One bracketed paste (a slash command: see <see cref="Pastes"/>), then Enter after <see cref="EnterDelay"/> (an Enter sent with the paste would be taken as
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
            var tail = await screen.ReadTailAsync(ScreenRows, styled: true);
            if (InBashMode(tail)) return BashModeFirst;
            if (text.StartsWith('/') && HasPromptText(tail)) return ClearInputFirst;
        }
        if (await FreshBlockedReasonAsync(s) is { } before) return before;
        foreach (var paste in Pastes(text))
            if (!s.SendText(paste)) return BlockedReason(s) ?? "Not sent";
        await Task.Delay(EnterDelay(text.Length));
        if (await FreshBlockedReasonAsync(s) is { } after) return after + ". The text is in the terminal input, not sent.";
        return s.SendKeys("\r") ? null : BlockedReason(s) ?? "Not sent";
    }

    internal const int PasteChunk = 500;

    /// <summary>The pastes <paramref name="text"/> goes in as: one, but a slash command as several of at most
    /// <see cref="PasteChunk"/> characters and one line break each (at its start). Claude Code folds a paste over 800
    /// characters or 3 lines into "[Pasted text #1]", and a folded slash command is sent as a plain prompt.
    /// shortcut: a terminal under about 12 rows may fold even a one-break paste; send breaks some other way if that shows up.</summary>
    internal static IEnumerable<string> Pastes(string text)
    {
        if (!text.StartsWith('/')) { yield return text; yield break; }
        text = text.ReplaceLineEndings("\r");
        for (var at = 0; at < text.Length;)
        {
            var end = Math.Min(text.Length, at + PasteChunk);
            if (text.IndexOf('\r', at + 1, end - at - 1) is >= 0 and var line) end = line;
            else if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--; // never split a surrogate pair
            yield return text[at..end];
            at = end;
        }
    }

    /// <summary>A long paste takes the TUI longer to take in: 80 ms + 1 ms per 100 characters, at most 1 s.</summary>
    public static TimeSpan EnterDelay(int length) => TimeSpan.FromMilliseconds(Math.Min(1000, 80 + length / 100));

    /// <summary>True when the TUI's input holds typed text: its prompt row, a "❯ ", "> " or bash-mode "! " row, has typed text or
    /// continuation rows. A styled screen (<c>ReadTailAsync(styled: true)</c>) tells Claude Code's greyed placeholder, a
    /// suggested prompt included, from typed text; on a plain one only the default 'Try "…"' placeholder is told apart.
    /// See <see cref="PromptOf"/>.</summary>
    public static bool HasPromptText(string? screen) => PromptOf(screen) is { Text: true };

    /// <summary>True when the TUI's prompt row starts with the bash-mode "!", typed text or not.</summary>
    public static bool InBashMode(string? screen) => PromptOf(screen) is { Marker: '!' };

    /// <summary>The prompt Claude Code suggests, greyed in its empty input, from a styled screen; null when the input holds
    /// typed text, shows the default 'Try "…"' hint or nothing.
    /// shortcut: a suggestion that wraps onto a second row counts as typed text; join dim continuation rows if that shows up.</summary>
    public static string? SuggestionOf(string? styledScreen) =>
        PromptOf(styledScreen) is { Placeholder: { } p } && !p.StartsWith("Try \"") ? p : null;

    static readonly Regex Rule = new(@"^\s*[╭┌╰└]?─{3,}"), PromptRow = new(@"^\s*│?\s*([❯>!])(?:\s+(.*?))?\s*│?\s*$"), Sgr = new(@"\x1b\[(\d*)m");

    /// <summary>The last prompt row that has a ─ rule (or box border) right above it and another below it (after the
    /// continuation rows of a multi-line input), as Claude Code frames its input; so echoed prompts in the transcript and
    /// a status line under the box do not count. Null when none. Placeholder is the row's text when it is drawn as one.</summary>
    static (char Marker, bool Text, string? Placeholder)? PromptOf(string? screen)
    {
        var styled = (screen ?? "").Split('\n').Where(l => Plain(l).Trim().Length > 0).ToList();
        var rows = styled.Select(Plain).ToList();
        for (var i = rows.Count - 2; i > 0; i--)
        {
            if (!Rule.IsMatch(rows[i - 1]) || PromptRow.Match(rows[i]) is not { Success: true } m) continue;
            var continued = rows.Skip(i + 1).TakeWhile(r => !Rule.IsMatch(r)).Count();
            if (i + 1 + continued == rows.Count) continue; // no rule below
            var typed = m.Groups[2];
            var placeholder = continued == 0 && IsPlaceholder(Cells(styled[i]).Skip(typed.Index).Take(typed.Length)) ? typed.Value : null;
            return (m.Groups[1].Value[0], continued > 0 || (typed.Length > 0 && placeholder is null && !typed.Value.StartsWith("Try \"")), placeholder);
        }
        return null;
    }

    static string Plain(string row) => Sgr.Replace(row, "");

    /// <summary>The row's characters with the dim and inverse marks of ccw.tail's styled mode; they line up with <see cref="Plain"/>.</summary>
    static List<(char C, bool Dim, bool Inverse)> Cells(string row)
    {
        var cells = new List<(char, bool, bool)>();
        bool dim = false, inverse = false;
        var at = 0;
        foreach (Match m in Sgr.Matches(row))
        {
            cells.AddRange(row[at..m.Index].Select(c => (c, dim, inverse)));
            at = m.Index + m.Length;
            switch (m.Groups[1].Value)
            {
                case "2": dim = true; break;
                case "22": dim = false; break;
                case "7": inverse = true; break;
                case "27": inverse = false; break;
            }
        }
        cells.AddRange(row[at..].Select(c => (c, dim, inverse)));
        return cells;
    }

    // Claude Code draws its placeholder dim, with the first character inverse while the terminal has focus (its cursor).
    static bool IsPlaceholder(IEnumerable<(char C, bool Dim, bool Inverse)> text)
    {
        var cells = text.Where(c => !char.IsWhiteSpace(c.C)).ToList();
        return cells.Any(c => c.Dim) && cells.Skip(cells[0].Inverse ? 1 : 0).All(c => c.Dim);
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
