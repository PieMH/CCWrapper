using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Threading;
using CCWrapper.Terminal;
using Microsoft.Web.WebView2.Core;

namespace CCWrapper.Views;

/// <summary>The Browser tool's hands: Claude's calls drive this panel's own WebView2, in-process (no remote debugging port).</summary>
public partial class BrowserPanel : IBrowserDriver
{
    static readonly TimeSpan LoadLimit = TimeSpan.FromSeconds(30), StartLimit = TimeSpan.FromSeconds(20), ActionLimit = TimeSpan.FromSeconds(3);

    readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string> NavigateAsync(string url, CancellationToken ct)
    {
        url = NormalizeUrl(url);
        return SchemeAllowed(url) ? NavigateAllowedAsync(url, ct)
            : Task.FromException<string>(new InvalidOperationException($"Refused {url}: the navigate tool opens only http, https, blob, about:blank and data addresses."));
    }

    Task<string> NavigateAllowedAsync(string url, CancellationToken ct) => OnUi(ct, async core =>
    {
        var done = await AfterAsync(() => { Navigate(url); return Task.CompletedTask; }, LoadLimit, certain: true, ct);
        return done is null ? $"Still loading after 30 s: {core.Source}"
            : done.IsSuccess ? $"Loaded {core.Source} (title: {core.DocumentTitle})"
            : $"Could not load {url}: {done.WebErrorStatus}";
    }, anyPage: true);

    public Task<string> SnapshotAsync(CancellationToken ct) =>
        OnUi(ct, async _ => (string)(await Js(BrowserScripts.Snapshot))["value"]!);

    public Task<string> ClickAsync(string target, CancellationToken ct) => OnUi(ct, async core =>
    {
        await AfterAsync(async () =>
        {
            if ((await Js(BrowserScripts.Call(BrowserScripts.Click, BrowserScripts.Selector(target), target)))["value"] is not JsonArray at) return;
            double x = (double)at[0]!, y = (double)at[1]!;
            await Mouse("mouseMoved", x, y);
            await Mouse("mousePressed", x, y);
            await Mouse("mouseReleased", x, y);
        }, ActionLimit, certain: false, ct);
        return $"Clicked {target}. URL: {core.Source}";
    });

    public Task<string> TypeAsync(string target, string text, bool submit, CancellationToken ct) => OnUi(ct, async core =>
    {
        async Task Enter()
        {
            foreach (var type in new[] { "keyDown", "keyUp" })
                await Cdp("Input.dispatchKeyEvent", new JsonObject
                {
                    ["type"] = type, ["key"] = "Enter", ["code"] = "Enter", ["windowsVirtualKeyCode"] = 13, ["text"] = type == "keyDown" ? "\r" : "",
                });
        }
        await Js(BrowserScripts.Call(BrowserScripts.Focus, BrowserScripts.Selector(target), target));
        if (text.Length > 0) await Cdp("Input.insertText", new JsonObject { ["text"] = text });
        else await Js("document.execCommand('delete')"); // insertText of nothing would leave the old value
        if (submit) await AfterAsync(Enter, ActionLimit, certain: false, ct);
        return $"Typed {text.Length} characters into {target}{(submit ? " and pressed Enter" : "")}. URL: {core.Source}";
    });

    public Task<byte[]> ScreenshotAsync(CancellationToken ct) => OnUi(ct, async core =>
    {
        using var png = new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
        return png.ToArray();
    });

    public Task<string> EvaluateAsync(string expression, CancellationToken ct) => OnUi(ct, async _ =>
    {
        var result = await Js(expression);
        return result["type"]?.ToString() == "undefined" ? "undefined" : result["value"]?.ToJsonString() ?? "null";
    });

    public Task<string> DevToolsAsync(string method, string paramsJson, CancellationToken ct) => OnUi(ct, core =>
    {
        if (JsonNode.Parse(paramsJson) is not JsonObject) throw new ArgumentException("'params' must be a JSON object.");
        return core.CallDevToolsProtocolMethodAsync(method, paramsJson);
    });

    /// <summary>On the UI thread, with the panel in front and its WebView2 started: a TabControl loads only the selected tab.
    /// Refuses, before and after the body, unless the page shown is a web page (history, an address-bar file: page...); only navigate passes <paramref name="anyPage"/>.</summary>
    async Task<T> OnUi<T>(CancellationToken ct, Func<CoreWebView2, Task<T>> body, bool anyPage = false) => await await Dispatcher.InvokeAsync(async () =>
    {
        if (Parent is TabItem { IsSelected: false } tab)
        {
            tab.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.Loaded); // let the panel lay out, so a screenshot is not a blank frame
        }
        try { await _ready.Task.WaitAsync(StartLimit, ct); }
        catch (TimeoutException) { throw new InvalidOperationException("The Browser panel's WebView2 did not start within 20 s."); }
        async Task Check()
        {
            var source = Web.CoreWebView2.Source;
            if (source.Length == 0) source = (string?)(await Js("location.href"))["value"] ?? ""; // WebView2 reports no Source for a data: page
            if (NotAWebPage(source) is { } refusal) throw new InvalidOperationException(refusal);
        }
        if (!anyPage) await Check();
        var result = await body(Web.CoreWebView2).WaitAsync(ct);
        if (!anyPage) await Check(); // a history move the body scheduled may have committed a local page meanwhile: never return what it read
        return result;
    }, DispatcherPriority.Normal, ct);

    Task<string> Cdp(string method, JsonObject args) => Web.CoreWebView2.CallDevToolsProtocolMethodAsync(method, args.ToJsonString());

    Task Mouse(string type, double x, double y) => Cdp("Input.dispatchMouseEvent",
        new JsonObject { ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = "left", ["buttons"] = type == "mousePressed" ? 1 : 0, ["clickCount"] = 1 });

    /// <summary>Runtime.evaluate in the page; the result object ({type, value}). A page exception throws with its message.</summary>
    async Task<JsonNode> Js(string expression)
    {
        var reply = JsonNode.Parse(await Cdp("Runtime.evaluate",
            new JsonObject { ["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = true }))!;
        if (reply["exceptionDetails"] is { } ex)
        {
            var message = ((ex["exception"]?["description"] ?? ex["exception"]?["value"] ?? ex["text"])?.ToString() ?? "Script error").Split('\n')[0];
            throw new InvalidOperationException(message.StartsWith("Error: ") ? message[7..] : message);
        }
        return reply["result"]!;
    }

    /// <summary>Runs <paramref name="act"/> and returns how the navigation it started ended, or null if none started (unless
    /// <paramref name="certain"/>, which waits for one) or it was still loading after <paramref name="limit"/>.</summary>
    async Task<CoreWebView2NavigationCompletedEventArgs?> AfterAsync(Func<Task> act, TimeSpan limit, bool certain, CancellationToken ct)
    {
        var core = Web.CoreWebView2;
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
