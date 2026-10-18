using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Threading;
using CCWrapper.Terminal;
using Microsoft.Web.WebView2.Core;

namespace CCWrapper.Views;

/// <summary>The Browser tool's hands: Claude's calls drive this panel's own WebView2s, in-process (no remote debugging port).
/// Every tool but the tab tools acts on the front tab.</summary>
public partial class BrowserPanel : IBrowserDriver
{
    static readonly TimeSpan LoadLimit = TimeSpan.FromSeconds(30), StartLimit = TimeSpan.FromSeconds(20), ActionLimit = TimeSpan.FromSeconds(3);

    public Task<string> NavigateAsync(string url, CancellationToken ct)
    {
        url = NormalizeUrl(url);
        return OpenRefusal("navigate", url) is { } why ? Task.FromException<string>(new InvalidOperationException(why))
            : OnUi(ct, core => LoadAsync(core, url, ct), anyPage: true);
    }

    async Task<string> LoadAsync(CoreWebView2 core, string url, CancellationToken ct)
    {
        var done = await AfterAsync(core, () => { core.Navigate(url); return Task.CompletedTask; }, LoadLimit, certain: true, ct);
        return done is null ? $"Still loading after 30 s: {core.Source}"
            : done.IsSuccess ? $"Loaded {core.Source} (title: {core.DocumentTitle})"
            : $"Could not load {url}: {done.WebErrorStatus}";
    }

    public Task<string> SnapshotAsync(CancellationToken ct) =>
        OnUi(ct, async core => (string)(await Js(core, BrowserScripts.Snapshot))["value"]!);

    public Task<string> ClickAsync(string target, CancellationToken ct) => OnUi(ct, async core =>
    {
        var front = _tabs.Front;
        await AfterAsync(core, async () =>
        {
            if ((await Js(core, BrowserScripts.Call(BrowserScripts.Click, BrowserScripts.Selector(target), target)))["value"] is not JsonArray at) return;
            double x = (double)at[0]!, y = (double)at[1]!;
            await Mouse(core, "mouseMoved", x, y);
            await Mouse(core, "mousePressed", x, y);
            await Mouse(core, "mouseReleased", x, y);
        }, ActionLimit, certain: false, ct);
        return $"Clicked {target}. URL: {core.Source}{FrontNote(front)}";
    });

    public Task<string> TypeAsync(string target, string text, bool submit, CancellationToken ct) => OnUi(ct, async core =>
    {
        async Task Enter()
        {
            foreach (var type in new[] { "keyDown", "keyUp" })
                await Cdp(core, "Input.dispatchKeyEvent", new JsonObject
                {
                    ["type"] = type, ["key"] = "Enter", ["code"] = "Enter", ["windowsVirtualKeyCode"] = 13, ["text"] = type == "keyDown" ? "\r" : "",
                });
        }
        var front = _tabs.Front;
        await Js(core, BrowserScripts.Call(BrowserScripts.Focus, BrowserScripts.Selector(target), target));
        if (text.Length > 0) await Cdp(core, "Input.insertText", new JsonObject { ["text"] = text });
        else await Js(core, "document.execCommand('delete')"); // insertText of nothing would leave the old value
        if (submit) await AfterAsync(core, Enter, ActionLimit, certain: false, ct);
        return $"Typed {text.Length} characters into {target}{(submit ? " and pressed Enter" : "")}. URL: {core.Source}{FrontNote(front)}";
    });

    // A click that opened a new tab (target=_blank, window.open) moved the front.
    string FrontNote(int before) => _tabs.Front == before ? "" : $" Tab {_tabs.Front} is now in front; the other tools act on it (tabs lists the tabs).";

    public Task<byte[]> ScreenshotAsync(CancellationToken ct) => OnUi(ct, async core =>
    {
        using var png = new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
        return png.ToArray();
    });

    public Task<string> EvaluateAsync(string expression, CancellationToken ct) => OnUi(ct, async core =>
    {
        var result = await Js(core, expression);
        return result["type"]?.ToString() == "undefined" ? "undefined" : result["value"]?.ToJsonString() ?? "null";
    });

    public Task<string> DevToolsAsync(string method, string paramsJson, CancellationToken ct) => OnUi(ct, core =>
    {
        if (JsonNode.Parse(paramsJson) is not JsonObject) throw new ArgumentException("'params' must be a JSON object.");
        return core.CallDevToolsProtocolMethodAsync(method, paramsJson);
    });

    public Task<string> TabsAsync(CancellationToken ct) => Ui(ct, async () =>
    {
        var rows = new List<(int, string?, string?, bool)>();
        foreach (var (id, tab) in _tabs.All.ToList())
        {
            string? title = "(not started)", url = tab.Pending ?? "about:blank";
            if (tab.Ready.IsCompletedSuccessfully)
            {
                var core = tab.Web.CoreWebView2;
                title = core.DocumentTitle;
                try { url = await SourceOf(core).WaitAsync(ActionLimit, ct); }
                catch (Exception) when (!ct.IsCancellationRequested) { url = null; } // a page that does not answer lists as not a web page
            }
            rows.Add((id, title, url, id == _tabs.Front));
        }
        return DescribeTabs(rows);
    });

    public Task<string> NewTabAsync(string url, CancellationToken ct)
    {
        url = NormalizeUrl(url);
        return OpenRefusal("new_tab", url) is { } why ? Task.FromException<string>(new InvalidOperationException(why)) : Ui(ct, async () =>
        {
            var tab = AddTab() ?? throw new InvalidOperationException($"The Browser panel already has {BrowserTabs<Tab>.Max} tabs; close one first.");
            return $"Opened tab {tab.Id} in front. " + await LoadAsync(await Started(tab, ct), url, ct);
        });
    }

    public Task<string> SwitchTabAsync(int id, CancellationToken ct) => Ui(ct, () =>
    {
        Show(_tabs[id] ?? throw NoTab(id));
        return Task.FromResult($"Tab {id} is in front; the other tools act on it.");
    });

    public Task<string> CloseTabAsync(int id, CancellationToken ct) => Ui(ct, () =>
        Task.FromResult(CloseTab(id) ? $"Closed tab {id}. Tab {_tabs.Front} is in front." : throw NoTab(id)));

    static InvalidOperationException NoTab(int id) => new($"There is no tab {id}; the tabs tool lists the open tabs.");

    /// <summary>On the UI thread, with the panel in front: a TabControl loads only the selected tab.</summary>
    async Task<T> Ui<T>(CancellationToken ct, Func<Task<T>> body) => await await Dispatcher.InvokeAsync(async () =>
    {
        if (Parent is TabItem { IsSelected: false } item)
        {
            item.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.Loaded); // let the panel lay out, so a screenshot is not a blank frame
        }
        return await body().WaitAsync(ct);
    }, DispatcherPriority.Normal, ct);

    /// <summary>Runs <paramref name="body"/> on the front tab's WebView2, once started. That tab stays the one the call works on even
    /// if another comes to front meanwhile. Refuses, before and after the body, unless its page is a web page (history, an
    /// address-bar file: page...); only navigate passes <paramref name="anyPage"/>.</summary>
    Task<T> OnUi<T>(CancellationToken ct, Func<CoreWebView2, Task<T>> body, bool anyPage = false) => Ui(ct, async () =>
    {
        var tab = Front;
        var core = await Started(tab, ct);
        async Task Check()
        {
            if (_tabs[tab.Id] != tab) throw new InvalidOperationException($"Tab {tab.Id} was closed during the call."); // fail closed: nothing left to check
            if (NotAWebPage(await SourceOf(core)) is { } refusal) throw new InvalidOperationException(refusal);
        }
        if (!anyPage) await Check();
        var result = await body(core).WaitAsync(ct);
        if (!anyPage) await Check(); // a history move the body scheduled may have committed a local page meanwhile: never return what it read
        return result;
    });

    static async Task<CoreWebView2> Started(Tab tab, CancellationToken ct)
    {
        try { await tab.Ready.WaitAsync(StartLimit, ct); }
        catch (TimeoutException) { throw new InvalidOperationException("The Browser panel's WebView2 did not start within 20 s."); }
        return tab.Web.CoreWebView2;
    }

    // WebView2 reports no Source for a data: page
    static async Task<string> SourceOf(CoreWebView2 core) =>
        core.Source is { Length: > 0 } source ? source : (string?)(await Js(core, "location.href"))["value"] ?? "";

    static Task<string> Cdp(CoreWebView2 core, string method, JsonObject args) => core.CallDevToolsProtocolMethodAsync(method, args.ToJsonString());

    static Task Mouse(CoreWebView2 core, string type, double x, double y) => Cdp(core, "Input.dispatchMouseEvent",
        new JsonObject { ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = "left", ["buttons"] = type == "mousePressed" ? 1 : 0, ["clickCount"] = 1 });

    /// <summary>Runtime.evaluate in the page; the result object ({type, value}). A page exception throws with its message.</summary>
    static async Task<JsonNode> Js(CoreWebView2 core, string expression)
    {
        var reply = JsonNode.Parse(await Cdp(core, "Runtime.evaluate",
            new JsonObject { ["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = true }))!;
        if (reply["exceptionDetails"] is { } ex)
        {
            var message = ((ex["exception"]?["description"] ?? ex["exception"]?["value"] ?? ex["text"])?.ToString() ?? "Script error").Split('\n')[0];
            throw new InvalidOperationException(message.StartsWith("Error: ") ? message[7..] : message);
        }
        return reply["result"]!;
    }

    /// <summary>Runs <paramref name="act"/> and returns how the navigation it started on <paramref name="core"/> ended, or null if none
    /// started (unless <paramref name="certain"/>, which waits for one) or it was still loading after <paramref name="limit"/>.</summary>
    static async Task<CoreWebView2NavigationCompletedEventArgs?> AfterAsync(CoreWebView2 core, Func<Task> act, TimeSpan limit, bool certain, CancellationToken ct)
    {
        ulong id = 0;
        var started = false;
        var done = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStart(object? _, CoreWebView2NavigationStartingEventArgs e) { started = true; id = e.NavigationId; }
        void OnDone(object? _, CoreWebView2NavigationCompletedEventArgs e) { if (started && e.NavigationId == id) done.TrySetResult(e); }
        core.NavigationStarting += OnStart;
        core.NavigationCompleted += OnDone;
        try
        {
            await act();
            if (!certain) { await Task.Delay(250, ct); if (!started) return null; } // grace for a navigation the action triggers
            return await done.Task.WaitAsync(limit, ct);
        }
        catch (TimeoutException) { return null; }
        finally { core.NavigationStarting -= OnStart; core.NavigationCompleted -= OnDone; }
    }
}
