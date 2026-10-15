using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CCWrapper.Core;
using CCWrapper.Data;
using Microsoft.Web.WebView2.Core;

namespace CCWrapper.Terminal;

/// <summary>The center terminal: one WebView2 holding one xterm.js per open session (keyed by TabId), each fed by its
/// own claude.exe in a ConPTY. All members are for the UI thread.</summary>
public partial class TerminalHost : UserControl
{
    const string Origin = "https://ccw-terminal.example/";

    readonly Dictionary<Guid, Tab> _tabs = [];
    readonly TaskCompletionSource _pageReady = new();
    Task? _ready;
    Tab? _shown; // the terminal the page is showing

    /// <summary>The exit overlay's Close button was clicked; the handler removes the session and calls <see cref="Close"/>.</summary>
    public event EventHandler<LiveSession>? CloseRequested;

    public TerminalHost()
    {
        InitializeComponent();
        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1e, 0x1e, 0x1e);
        Loaded += (_, _) => _ = Ready;
        Dispatcher.ShutdownStarted += (_, _) =>
        {
            foreach (var tab in _tabs.Values) { tab.Feed?.Dispose(); tab.Pty?.Dispose(); }
        };
    }

    Task Ready => _ready ??= InitAsync();

    /// <summary>Starts claude.exe for the session in a new terminal (or its exited one) and shows it. Sets SessionId
    /// (a new uuid unless resuming), Cwd, TranscriptPath, Status and, while running, Channel; Status becomes Exited
    /// when claude.exe ends. A failed start is shown in the terminal; throws only if WebView2 cannot start.</summary>
    public async Task OpenAsync(LiveSession session, LaunchOptions options)
    {
        if (_tabs.TryGetValue(session.TabId, out var running) && (running.Pty is not null || running.Size is not null))
        {
            Show(session); // already running or starting
            return;
        }
        var id = options.ResumeId ?? Guid.NewGuid().ToString();
        session.SessionId = id;
        session.Cwd = options.Cwd;
        session.TranscriptPath = session.Account.Paths.Transcript(options.Cwd, id);
        var tab = GetOrAdd(session);
        tab.Options = options;
        tab.Feed ??= new StatusLineFeed(session);
        var args = ClaudeLaunch.Args(options, id, ClaudeLaunch.RelaySettings(session.Account, options.Cwd));
        await StartAsync(tab, ClaudeLaunch.FindClaude(), args, options.Cwd,
            ClaudeLaunch.Env(session.Account, session.TabId, Environment.GetEnvironmentVariables()));
    }

    /// <summary>Relaunches an exited session in its terminal with <c>--resume &lt;SessionId&gt;</c> (the overlay's Resume).</summary>
    public Task ResumeAsync(LiveSession session) =>
        _tabs.TryGetValue(session.TabId, out var tab) && tab.Pty is null && tab.Size is null && tab.Options is { } o && session.SessionId is { } id
            ? OpenAsync(session, o with { ResumeId = id, Name = null })
            : Task.CompletedTask;

    /// <summary>Brings the session's terminal forward; the others keep running hidden.</summary>
    public void Show(LiveSession session)
    {
        if (!_tabs.TryGetValue(session.TabId, out var tab)) return;
        _shown = tab;
        Post(new { t = "show", id = tab.Id });
        Web.Focus();
    }

    /// <summary>Kills the session's claude.exe and removes its terminal.</summary>
    public void Close(LiveSession session)
    {
        if (!_tabs.Remove(session.TabId, out var tab)) return;
        var pty = tab.Pty;
        tab.Pty = null;
        pty?.Dispose();
        tab.Size?.TrySetResult((0, 0)); // unblock a start still waiting for the page
        tab.Feed?.Dispose();
        session.Channel = null;
        session.Pid = null;
        session.Status = SessionStatus.Exited;
        Post(new { t = "close", id = tab.Id });
    }

    /// <summary>Smoke test: any program instead of claude.exe.</summary>
    internal Task OpenProcessAsync(LiveSession session, string app, List<string> args, string cwd) =>
        StartAsync(GetOrAdd(session), app, args, cwd, ClaudeLaunch.Env(session.Account, session.TabId, Environment.GetEnvironmentVariables()));

    /// <summary>The terminal's buffer as text, one line per row.</summary>
    internal async Task<string?> ReadTextAsync(LiveSession session) =>
        JsonSerializer.Deserialize<string?>(await Web.CoreWebView2.ExecuteScriptAsync($"ccw.text('{session.TabId}')"));

    /// <summary>The last <paramref name="rows"/> non-blank rows of the terminal's screen (not the scrollback), one line per row.</summary>
    internal async Task<string?> ReadTailAsync(LiveSession session, int rows) =>
        JsonSerializer.Deserialize<string?>(await Web.CoreWebView2.ExecuteScriptAsync($"ccw.tail('{session.TabId}', {rows})"));

    Tab GetOrAdd(LiveSession session)
    {
        if (!_tabs.TryGetValue(session.TabId, out var tab)) _tabs[session.TabId] = tab = new Tab(this, session);
        return tab;
    }

    async Task StartAsync(Tab tab, string app, List<string> args, string cwd, Dictionary<string, string> env)
    {
        await Ready;
        tab.Size = new();
        _shown = tab;
        Post(new { t = "open", id = tab.Id }); // the page shows the terminal, fits it and answers with its size
        var (cols, rows) = await tab.Size.Task;
        tab.Size = null;
        if (!_tabs.ContainsValue(tab)) return; // closed meanwhile

        tab.Session.Status = SessionStatus.Unknown;
        PtySession? pty = null;
        try
        {
            pty = await PtySession.StartAsync(app, args, cwd, env, cols, rows,
                text => OnOutput(tab, text),
                code => Dispatcher.BeginInvoke(() => OnExited(tab, pty, code))); // queued behind the assignment below
        }
        catch (Exception ex)
        {
            Post(new { t = "out", id = tab.Id, d = $"\x1b[31mCould not start {app}: {ex.Message}\x1b[0m\r\n" });
            OnExited(tab, null, -1);
            return;
        }
        if (!_tabs.ContainsValue(tab)) { pty.Dispose(); return; }
        tab.Pty = pty;
        tab.Session.Pid = pty.Pid;
        tab.Session.Channel = tab;
    }

    // Reader thread: coalesce output for ~10 ms so the page gets few, large writes.
    void OnOutput(Tab tab, string text)
    {
        lock (tab.Pending)
        {
            tab.Pending.Append(text);
            if (tab.FlushQueued) return;
            tab.FlushQueued = true;
        }
        Task.Delay(10).ContinueWith(_ => Dispatcher.BeginInvoke(() => Flush(tab)));
    }

    void Flush(Tab tab)
    {
        string text;
        lock (tab.Pending)
        {
            text = tab.Pending.ToString();
            tab.Pending.Clear();
            tab.FlushQueued = false;
        }
        if (text.Length > 0) Post(new { t = "out", id = tab.Id, d = text });
    }

    void OnExited(Tab tab, PtySession? pty, int code)
    {
        if (tab.Pty != pty || !_tabs.ContainsValue(tab)) return; // closed, or not the current process
        Flush(tab);
        tab.Pty = null;
        tab.Session.Channel = null;
        tab.Session.Pid = null;
        tab.Session.Status = SessionStatus.Exited;
        Post(new { t = "exit", id = tab.Id, code });
    }

    async Task InitAsync()
    {
        var letter = AppState.Current?.Account.Letter ?? "smoke";
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(ClaudeLaunch.AppDataDir, "WebView2", letter));
        await Web.EnsureCoreWebView2Async(env);
        var core = Web.CoreWebView2;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping(new Uri(Origin).Host,
            Path.Combine(AppContext.BaseDirectory, "Terminal", "wwwroot"), CoreWebView2HostResourceAccessKind.Deny);
        core.WebMessageReceived += OnMessage;
        core.ProcessFailed += OnProcessFailed;
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenLink(e.Uri); };
        core.NavigationStarting += (_, e) => e.Cancel = !e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase);
        core.Navigate(Origin + "index.html");
        await _pageReady.Task;
    }

    // Renderer crash: the xterm buffers and the acks that unblock PtySession are gone and claude.exe would stall. Choice: end every
    // running session (Resume restores it from its transcript) and reload, rather than re-attach to blank screens.
    void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited) return;
        foreach (var tab in _tabs.Values.Where(t => t.Pty is not null).ToList())
        {
            var pty = tab.Pty;
            tab.Pty = null;
            pty?.Dispose();
            lock (tab.Pending) tab.Pending.Clear();
            tab.Session.Channel = null;
            tab.Session.Pid = null;
            tab.Session.Status = SessionStatus.Exited;
        }
        Web.CoreWebView2.Reload();
    }

    // The reloaded page has no terminals: recreate them, with the exit overlay (Resume, Close) on the ended ones.
    void RestoreTerminals()
    {
        foreach (var tab in _tabs.Values)
        {
            Post(new { t = "open", id = tab.Id });
            if (tab.Size is null) Post(new { t = "exit", id = tab.Id, code = -1 }); // else a start is waiting for this open's size
        }
        if (_shown is { } shown && _tabs.ContainsValue(shown)) Post(new { t = "show", id = shown.Id });
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
        var m = doc.RootElement;
        string? S(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int I(string name) => m.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

        switch (S("t"))
        {
            case "ready":
                if (!_pageReady.TrySetResult()) RestoreTerminals(); // the page reloaded after a renderer crash
                return;
            case "copy": SetClipboard(S("d")); return;
            case "link": OpenLink(S("url")); return;
        }
        if (!Guid.TryParse(S("id"), out var tabId) || !_tabs.TryGetValue(tabId, out var tab)) return;
        switch (S("t"))
        {
            case "in": tab.Pty?.Write(S("d") ?? ""); break;
            case "bin": tab.Pty?.Write(Encoding.Latin1.GetBytes(S("d") ?? "")); break; // one char per byte (X10 mouse)
            case "ack": tab.Pty?.Ack(I("n")); break;
            case "resize":
                if (tab.Size is { } size) size.TrySetResult((I("cols"), I("rows")));
                else tab.Pty?.Resize(I("cols"), I("rows"));
                break;
            case "paste": Paste(tab); break;
            case "drop":
                var paths = e.AdditionalObjects.OfType<CoreWebView2File>().Select(f => f.Path).ToList();
                if (paths.Count > 0) Post(new { t = "paste", id = tab.Id, d = PasteHandler.QuotePaths(paths) });
                break;
            case "transcript": AppState.Current?.Commands.ShowTranscript(tab.Session, S("q")); break;
            case "resume": _ = ResumeAsync(tab.Session); break;
            case "close": CloseRequested?.Invoke(this, tab.Session); break;
        }
    }

    // Files → quoted paths and text → xterm paste (bracketed); a bitmap alone → Alt+V so Claude Code reads it itself.
    void Paste(Tab tab)
    {
        string? text;
        bool image;
        try
        {
            if (Clipboard.GetDataObject() is not { } data) return;
            text = PasteHandler.Text(data);
            image = text is null && PasteHandler.HasImage(data);
        }
        catch (ExternalException) { return; } // clipboard held by another app
        if (text is not null) Post(new { t = "paste", id = tab.Id, d = text });
        else if (image) tab.Pty?.Write(PasteHandler.ImagePasteKeys);
    }

    static void SetClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (ExternalException) { } // clipboard held by another app
    }

    // Terminal output is untrusted: only http(s) leaves the app. Local servers go to the Browser panel.
    static void OpenLink(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        if (uri.IsLoopback && AppState.Current is { } app) app.Commands.OpenInBrowser(uri.AbsoluteUri);
        else Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    void Post(object message) => Web.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(message));

    sealed class Tab(TerminalHost host, LiveSession session) : IScreenChannel
    {
        public LiveSession Session { get; } = session;
        public string Id { get; } = session.TabId.ToString();
        public PtySession? Pty;
        public LaunchOptions? Options;
        public StatusLineFeed? Feed;
        public TaskCompletionSource<(int Cols, int Rows)>? Size; // set while waiting for the page to size a new terminal
        public readonly StringBuilder Pending = new();
        public bool FlushQueued;

        // Claude Code always enables bracketed paste; control characters are dropped so the text cannot end the paste early.
        public void SendText(string text) => Pty?.Write(PasteHandler.Bracketed(text));

        public void SendKeys(string keys) => Pty?.Write(keys);

        public async Task<string?> ReadTailAsync(int rows)
        {
            try { return await host.ReadTailAsync(Session, rows); }
            catch (Exception) { return null; } // WebView2 not up yet, reloading or gone: callers run from async void handlers
        }
    }
}

/// <summary>A terminal whose screen can be read back. Must be called on the UI thread.</summary>
public interface IScreenChannel : ITerminalChannel
{
    /// <summary>The last <paramref name="rows"/> non-blank rows of the screen, one line per row; null when it cannot be read.</summary>
    Task<string?> ReadTailAsync(int rows);
}
