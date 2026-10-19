using System.Text.Json;
using System.Text.Json.Nodes;

namespace CCWrapper.Terminal;

/// <summary>What the <c>ccwrapper-browser</c> MCP tools do to CCWrapper's Browser panel. Every member may be called from any
/// thread; an implementation marshals to the UI thread itself and brings the Browser panel into view when a call needs it.
/// Calls never overlap: <see cref="BrowserMcp"/> runs them one at a time across every tab, except that a call past its time
/// limit is abandoned (its token cancelled) and the next one may start while it lingers. A failure throws, and the
/// exception's message is what Claude reads, so make it say what went wrong ("No element matches #login").</summary>
/// <remarks>A <c>target</c> that is all ASCII digits is a ref number from the latest <see cref="SnapshotAsync"/>; anything
/// else is a CSS selector, and the first element it matches is used. A ref that no longer exists throws.
/// <para>The panel has tabs of its own, each with an id (1, 2, 3…, never reused). Every member but the four tab members acts on
/// the front tab; a page's target=_blank or window.open opens a new tab in front.</para></remarks>
public interface IBrowserDriver
{
    /// <summary>Opens <paramref name="url"/> and waits for the page to load. Returns a short text for Claude, e.g. the final
    /// URL and the page title.</summary>
    Task<string> NavigateAsync(string url, CancellationToken ct);

    /// <summary>The current page as text: an outline of headings, text, links, buttons and form fields, each element that can
    /// be clicked or typed into tagged with a ref number (<c>[12]</c>). Each snapshot renumbers the refs.</summary>
    Task<string> SnapshotAsync(CancellationToken ct);

    /// <summary>Scrolls the target into view and clicks it. Returns a short text, e.g. the URL after the click.</summary>
    Task<string> ClickAsync(string target, CancellationToken ct);

    /// <summary>Focuses the target (an input, textarea or editable element), replaces its value with <paramref name="text"/>
    /// as typed input (so the page's input events fire) and, with <paramref name="submit"/>, presses Enter. Returns a short text.</summary>
    Task<string> TypeAsync(string target, string text, bool submit, CancellationToken ct);

    /// <summary>The visible part of the page as PNG bytes.</summary>
    Task<byte[]> ScreenshotAsync(CancellationToken ct);

    /// <summary>Runs a JavaScript expression in the page and returns its value serialized as JSON; a returned Promise is
    /// awaited. A thrown exception in the page throws here with its message.</summary>
    Task<string> EvaluateAsync(string expression, CancellationToken ct);

    /// <summary>Calls one Chrome DevTools Protocol method on the page (<paramref name="paramsJson"/> is a JSON object) and
    /// returns its JSON result.</summary>
    Task<string> DevToolsAsync(string method, string paramsJson, CancellationToken ct);

    /// <summary>The open tabs as text: id, title and URL of each, and which one is in front.</summary>
    Task<string> TabsAsync(CancellationToken ct);

    /// <summary>Opens a new tab in front, loads <paramref name="url"/> in it and waits for the page to load.</summary>
    Task<string> NewTabAsync(string url, CancellationToken ct);

    /// <summary>Brings tab <paramref name="id"/> to front. An unknown id throws.</summary>
    Task<string> SwitchTabAsync(int id, CancellationToken ct);

    /// <summary>Closes tab <paramref name="id"/>; closing the last one leaves one blank tab. An unknown id throws.</summary>
    Task<string> CloseTabAsync(int id, CancellationToken ct);
}

/// <summary>The MCP server of the Browser tool, without its transport: one newline-delimited JSON-RPC 2.0 message in, zero
/// or one response line out. One instance serves every connection, so its queue runs tool calls one at a time across tabs.</summary>
/// <param name="enabled">Read on each tools/call: the Settings switch.</param>
/// <param name="callLimit">How long one tools/call may hold the queue before it is abandoned. Default 60 s.</param>
public sealed class BrowserMcp(IBrowserDriver driver, Func<bool> enabled, TimeSpan? callLimit = null)
{
    public const string Disabled = "The Browser tool is turned off in CCWrapper Settings.";

    readonly SemaphoreSlim _queue = new(1, 1);
    readonly TimeSpan _limit = callLimit ?? TimeSpan.FromSeconds(60);

    const string DevToolsLimits = "devtools reaches only the Accessibility, Animation, CSS, DOM, DOMSnapshot, Emulation, Log, Overlay, Page, " +
        "Performance, Runtime and Network domains, and refuses cookie access (Network.getAllCookies, getCookies, setCookie, setCookies, " +
        "deleteCookies, clearBrowserCookies, Page.getCookies, Page.deleteCookie), Network.loadNetworkResource, DOM.setFileInputFiles, " +
        "Page.setDownloadBehavior, Page.addScriptToEvaluateOnNewDocument, Page.addScriptToEvaluateOnLoad, Page.navigateToHistoryEntry, Page.getNavigationHistory and Page.navigate (use the navigate tool).";

    static readonly HashSet<string> DevToolsDomains = new(["Accessibility", "Animation", "CSS", "DOM", "DOMSnapshot", "Emulation", "Log",
        "Overlay", "Page", "Performance", "Runtime", "Network"], StringComparer.OrdinalIgnoreCase);

    // What would reach past the page: other sites' cookies, local files, the disk, every future page.
    static readonly HashSet<string> DevToolsDenied = new(["DOM.setFileInputFiles", "Page.setDownloadBehavior",
        "Page.addScriptToEvaluateOnNewDocument", "Page.navigate", "Network.getAllCookies", "Network.getCookies", "Network.setCookie",
        "Network.setCookies", "Network.deleteCookies", "Network.clearBrowserCookies", "Network.loadNetworkResource",
        "Page.addScriptToEvaluateOnLoad", "Page.getCookies", "Page.deleteCookie", "Page.navigateToHistoryEntry", "Page.getNavigationHistory"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when the devtools tool may call <paramref name="method"/>; otherwise the refusal Claude reads.</summary>
    public static string? DevToolsRefusal(string method)
    {
        if (method.Equals("Page.navigate", StringComparison.OrdinalIgnoreCase)) return "Refused Page.navigate: use the navigate tool.";
        return method.Split('.') is [var domain, var name] && name.Length > 0 && name.All(char.IsAsciiLetter)
            && DevToolsDomains.Contains(domain) && !DevToolsDenied.Contains(method)
            ? null : $"Refused {method}: {DevToolsLimits}";
    }

    /// <summary>The reply line (without its newline), or null for a notification. Throws only when <paramref name="ct"/> is cancelled.</summary>
    public async Task<string?> HandleAsync(string line, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        JsonObject? msg;
        try { msg = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }
        if (msg is null) return Error(null, -32600, "Invalid Request");

        var hasId = msg.ContainsKey("id");
        var id = msg["id"];
        if (msg["method"] is not JsonValue m || !m.TryGetValue(out string? method))
            return hasId ? Error(id, -32600, "Invalid Request") : null; // a client's response or junk: nothing to answer
        if (!hasId) return null; // notifications/* (initialized, cancelled, ...) never get a reply
        var p = msg["params"] as JsonObject;

        return method switch
        {
            "initialize" => Result(id, new JsonObject
            {
                ["protocolVersion"] = Text(p?["protocolVersion"]) is var v && v is "2024-11-05" or "2025-03-26" or "2025-06-18" ? v : "2025-06-18",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "ccwrapper-browser", ["version"] = typeof(BrowserMcp).Assembly.GetName().Version?.ToString(3) ?? "0" },
            }),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => Result(id, new JsonObject { ["tools"] = JsonNode.Parse(Tools) }),
            "tools/call" => await CallAsync(id, p, ct),
            _ => Error(id, -32601, "Method not found: " + method),
        };
    }

    async Task<string> CallAsync(JsonNode? id, JsonObject? p, CancellationToken ct)
    {
        Func<CancellationToken, Task<JsonObject>> run;
        try
        {
            var args = p?["arguments"] switch
            {
                null => new JsonObject(),
                JsonObject o => o,
                _ => throw new ArgumentException("'arguments' must be an object."),
            };
            // Every argument is read here, before the queue, so a bad one answers -32602 at once.
            run = Text(p?["name"]) switch
            {
                "navigate" => Run(Arg(args, "url"), driver.NavigateAsync),
                "snapshot" => c => TextResult(driver.SnapshotAsync(c)),
                "click" => Run(Arg(args, "target"), driver.ClickAsync),
                "type" => Run((Target: Arg(args, "target"), Text: Arg(args, "text"), Submit: args["submit"]?.GetValueKind() == JsonValueKind.True),
                    (a, c) => driver.TypeAsync(a.Target, a.Text, a.Submit, c)),
                "screenshot" => async c => Content(new JsonObject
                {
                    ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(await driver.ScreenshotAsync(c)),
                }),
                "evaluate" => Run(Arg(args, "expression"), driver.EvaluateAsync),
                "devtools" => Run((Method: Arg(args, "method"), Params: args["params"] switch
                {
                    null => "{}",
                    JsonObject o => o.ToJsonString(),
                    _ => throw new ArgumentException("'params' must be an object."),
                }), (a, c) => DevToolsRefusal(a.Method) is { } why
                    ? Task.FromException<string>(new InvalidOperationException(why))
                    : driver.DevToolsAsync(a.Method, a.Params, c)),
                "tabs" => c => TextResult(driver.TabsAsync(c)),
                "new_tab" => Run(args["url"] is null ? "about:blank" : Arg(args, "url"), driver.NewTabAsync),
                "switch_tab" => Run(Id(args), driver.SwitchTabAsync),
                "close_tab" => Run(Id(args), driver.CloseTabAsync),
                var name => throw new ArgumentException("Unknown tool: " + name),
            };
        }
        catch (ArgumentException ex) { return Error(id, -32602, ex.Message); }

        if (!enabled()) return Result(id, Failure(Disabled));
        await _queue.WaitAsync(ct);
        // A hung page (a dialog open, a Promise that never settles) must not hold every tab's calls forever.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_limit);
        try { return Result(id, await run(limit.Token).WaitAsync(limit.Token)); }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Result(id, Failure($"The Browser panel did not answer within {_limit.TotalSeconds:0} s (a page dialog may be open, or the page is busy); the call was abandoned."));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { return Result(id, Failure(ex.Message)); }
        finally { _queue.Release(); }
    }

    // A ref may arrive as a JSON number; anything else that is not a string is a bad param.
    static string Arg(JsonObject args, string key) => args[key] switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
        _ => throw new ArgumentException($"Missing or invalid '{key}'."),
    };

    static int Id(JsonObject args) => args["id"] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out int id)
        ? id : throw new ArgumentException("Missing or invalid 'id': a tab id (an integer) from the tabs tool.");

    static Func<CancellationToken, Task<JsonObject>> Run<T>(T args, Func<T, CancellationToken, Task<string>> call) => c => TextResult(call(args, c));

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    static async Task<JsonObject> TextResult(Task<string> text) => Content(new JsonObject { ["type"] = "text", ["text"] = await text });

    static JsonObject Content(JsonObject item) => new() { ["content"] = new JsonArray(item) };

    static JsonObject Failure(string message)
    {
        var result = Content(new JsonObject { ["type"] = "text", ["text"] = message });
        result["isError"] = true;
        return result;
    }

    static string Result(JsonNode? id, JsonNode result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result }.ToJsonString();

    static string Error(JsonNode? id, int code, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();

    const string TabId = """{ "type": "integer", "description": "A tab id from the tabs tool." }""";

    const string Target = """{ "type": "string", "description": "A ref number from the latest snapshot (e.g. \"12\"), or a CSS selector." }""";

    const string Tools = $$"""
        [
          { "name": "navigate",
            "description": "Open a URL in the front tab of CCWrapper's Browser panel (the browser inside this app, beside the terminal) and wait for it to load. The panel has tabs: every tool but tabs, new_tab, switch_tab and close_tab acts on the front tab, and a link or window.open that opens a new window opens a new tab in front.",
            "inputSchema": { "type": "object", "properties": { "url": { "type": "string", "description": "The address to open, e.g. https://example.com." } }, "required": ["url"] } },
          { "name": "snapshot",
            "description": "Read the front tab's page as text: headings, text, links, buttons and form fields, each element you can click or type into tagged with a ref number like [12]. Take a new snapshot after the page changes: each one renumbers the refs.",
            "inputSchema": { "type": "object", "properties": {} } },
          { "name": "click",
            "description": "Click an element of the front tab's page. If it opens a new tab, that tab comes to front and the result says so.",
            "inputSchema": { "type": "object", "properties": { "target": {{Target}} }, "required": ["target"] } },
          { "name": "type",
            "description": "Replace the value of an input, textarea or editable element of the front tab's page with text, as if typed. Set submit to press Enter afterwards.",
            "inputSchema": { "type": "object", "properties": { "target": {{Target}},
              "text": { "type": "string", "description": "The text to type." },
              "submit": { "type": "boolean", "description": "Press Enter after typing. Default false." } }, "required": ["target", "text"] } },
          { "name": "screenshot",
            "description": "Capture the visible part of the front tab's page as a PNG image.",
            "inputSchema": { "type": "object", "properties": {} } },
          { "name": "evaluate",
            "description": "Run a JavaScript expression in the front tab's page and return its value as JSON. A returned Promise is awaited.",
            "inputSchema": { "type": "object", "properties": { "expression": { "type": "string", "description": "The JavaScript expression, e.g. document.title." } }, "required": ["expression"] } },
          { "name": "devtools",
            "description": "Call a Chrome DevTools Protocol method on the front tab's page (e.g. Emulation.setDeviceMetricsOverride, Network.getResponseBody) and return its JSON result. For what the other tools cannot do. {{DevToolsLimits}}",
            "inputSchema": { "type": "object", "properties": {
              "method": { "type": "string", "description": "The CDP method, Domain.method." },
              "params": { "type": "object", "description": "The method's parameters. Default {}." } }, "required": ["method"] } },
          { "name": "tabs",
            "description": "List the Browser panel's tabs: the id, title and URL of each, and which one is in front (the one the other tools act on). A tab showing a page that is not a web address lists as (not a web page).",
            "inputSchema": { "type": "object", "properties": {} } },
          { "name": "new_tab",
            "description": "Open a new tab in front and load a URL in it; the other tools then act on it. At most 20 tabs.",
            "inputSchema": { "type": "object", "properties": { "url": { "type": "string", "description": "The address to open, e.g. https://example.com. Default about:blank." } } } },
          { "name": "switch_tab",
            "description": "Bring a tab to front, so the other tools act on it.",
            "inputSchema": { "type": "object", "properties": { "id": {{TabId}} }, "required": ["id"] } },
          { "name": "close_tab",
            "description": "Close a tab. Closing the last tab leaves one blank tab.",
            "inputSchema": { "type": "object", "properties": { "id": {{TabId}} }, "required": ["id"] } }
        ]
        """;
}
