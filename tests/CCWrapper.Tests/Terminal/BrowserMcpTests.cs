using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using CCWrapper.Terminal;

namespace CCWrapper.Tests.Terminal;

public sealed class BrowserMcpTests
{
    sealed class FakeDriver : IBrowserDriver
    {
        public readonly List<string> Calls = [];
        int _active;
        public int MaxActive;
        public TimeSpan Delay = TimeSpan.Zero;

        async Task<T> Do<T>(string call, T result)
        {
            lock (Calls) Calls.Add(call);
            var now = Interlocked.Increment(ref _active);
            lock (Calls) MaxActive = Math.Max(MaxActive, now);
            await Task.Delay(Delay);
            if (call.Contains("hang")) await new TaskCompletionSource().Task; // never answers, ignores its token too
            Interlocked.Decrement(ref _active);
            if (call.Contains("boom")) throw new InvalidOperationException("No element matches #boom");
            return result;
        }

        public Task<string> NavigateAsync(string url, CancellationToken ct) => Do($"navigate {url}", "loaded " + url);
        public Task<string> SnapshotAsync(CancellationToken ct) => Do("snapshot", "[1] button Go");
        public Task<string> ClickAsync(string target, CancellationToken ct) => Do($"click {target}", "clicked");
        public Task<string> TypeAsync(string target, string text, bool submit, CancellationToken ct) => Do($"type {target} {text} {submit}", "typed");
        public Task<byte[]> ScreenshotAsync(CancellationToken ct) => Do("screenshot", new byte[] { 0x89, 0x50, 0x4e, 0x47 });
        public Task<string> EvaluateAsync(string expression, CancellationToken ct) => Do($"evaluate {expression}", "\"Title\"");
        public Task<string> DevToolsAsync(string method, string paramsJson, CancellationToken ct) => Do($"devtools {method} {paramsJson}", "{}");
    }

    readonly FakeDriver _driver = new();
    bool _enabled = true;
    BrowserMcp Mcp() => new(_driver, () => _enabled);

    static string Call(int id, string name, string args = "{}") =>
        $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{name}}}","arguments":{{{args}}}}}""";

    static JsonNode Parse(string? line) => JsonNode.Parse(line!)!;

    [Fact]
    public async Task Initialize_echoes_the_protocol_version_and_names_the_server()
    {
        var r = Parse(await Mcp().HandleAsync("""{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{}}}"""));
        Assert.Equal("a", r["id"]!.GetValue<string>());
        Assert.Equal("2024-11-05", r["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(r["result"]!["capabilities"]!["tools"]);
        Assert.Equal("ccwrapper-browser", r["result"]!["serverInfo"]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2099-01-01", "2025-06-18")]
    [InlineData(null, "2025-06-18")]
    public async Task Initialize_answers_a_version_it_does_not_know_with_2025_06_18(string? asked, string answered)
    {
        var version = asked is null ? "" : $",\"protocolVersion\":\"{asked}\"";
        var r = Parse(await Mcp().HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}""" + version + "}}"));
        Assert.Equal(answered, r["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notifications_get_no_reply_and_ping_gets_an_empty_result()
    {
        var mcp = Mcp();
        Assert.Null(await mcp.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Null(await mcp.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1}}"""));
        Assert.Null(await mcp.HandleAsync("  "));
        Assert.Equal("""{"jsonrpc":"2.0","id":7,"result":{}}""", await mcp.HandleAsync("""{"jsonrpc":"2.0","id":7,"method":"ping"}"""));
    }

    [Fact]
    public async Task Tools_list_has_seven_tools_with_object_schemas()
    {
        var tools = Parse(await Mcp().HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""))["result"]!["tools"]!.AsArray();
        Assert.Equal(["navigate", "snapshot", "click", "type", "screenshot", "evaluate", "devtools"], tools.Select(t => t!["name"]!.GetValue<string>()));
        Assert.All(tools, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t!["description"]!.GetValue<string>()));
            Assert.Equal("object", t["inputSchema"]!["type"]!.GetValue<string>());
        });
    }

    [Theory]
    [InlineData("navigate", """{"url":"https://example.com"}""", "navigate https://example.com", "loaded https://example.com")]
    [InlineData("snapshot", "{}", "snapshot", "[1] button Go")]
    [InlineData("click", """{"target":12}""", "click 12", "clicked")] // a ref sent as a number
    [InlineData("click", """{"target":"#go"}""", "click #go", "clicked")]
    [InlineData("type", """{"target":"3","text":"hi","submit":true}""", "type 3 hi True", "typed")]
    [InlineData("type", """{"target":"3","text":"hi"}""", "type 3 hi False", "typed")]
    [InlineData("evaluate", """{"expression":"document.title"}""", "evaluate document.title", "\"Title\"")]
    [InlineData("devtools", """{"method":"DOM.getDocument"}""", "devtools DOM.getDocument {}", "{}")]
    [InlineData("devtools", """{"method":"Page.reload","params":{"ignoreCache":true}}""", """devtools Page.reload {"ignoreCache":true}""", "{}")]
    public async Task Tools_call_passes_arguments_and_returns_text(string tool, string args, string expectedCall, string expectedText)
    {
        var r = Parse(await Mcp().HandleAsync(Call(4, tool, args)))["result"]!;
        Assert.Equal([expectedCall], _driver.Calls);
        Assert.Equal("text", r["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(expectedText, r["content"]![0]!["text"]!.GetValue<string>());
        Assert.Null(r["isError"]);
    }

    [Fact]
    public async Task Screenshot_is_a_base64_png_image()
    {
        var item = Parse(await Mcp().HandleAsync(Call(1, "screenshot")))["result"]!["content"]![0]!;
        Assert.Equal(("image", "image/png", "iVBORw=="), (item["type"]!.GetValue<string>(), item["mimeType"]!.GetValue<string>(), item["data"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Driver_failure_is_a_tool_error_with_its_message()
    {
        var r = Parse(await Mcp().HandleAsync(Call(1, "click", """{"target":"#boom"}""")))["result"]!;
        Assert.True(r["isError"]!.GetValue<bool>());
        Assert.Equal("No element matches #boom", r["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_call_that_never_answers_is_abandoned_and_the_next_call_runs()
    {
        var mcp = new BrowserMcp(_driver, () => true, TimeSpan.FromMilliseconds(300));
        var r = Parse(await mcp.HandleAsync(Call(1, "evaluate", """{"expression":"hang"}""")).WaitAsync(TimeSpan.FromSeconds(10)))["result"]!;
        Assert.True(r["isError"]!.GetValue<bool>());
        var text = r["content"]![0]!["text"]!.GetValue<string>();
        Assert.StartsWith("The Browser panel did not answer within ", text);
        Assert.EndsWith(" s (a page dialog may be open, or the page is busy); the call was abandoned.", text);

        var next = Parse(await mcp.HandleAsync(Call(2, "snapshot")).WaitAsync(TimeSpan.FromSeconds(10)))["result"]!;
        Assert.Null(next["isError"]);
        Assert.Equal("[1] button Go", next["content"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Runtime.evaluate")]
    [InlineData("DOM.getDocument")]
    [InlineData("Emulation.setDeviceMetricsOverride")]
    [InlineData("Network.getResponseBody")]
    [InlineData("Page.captureScreenshot")]
    [InlineData("Accessibility.getFullAXTree")]
    public void Devtools_allows_methods_of_the_page_domains(string method) => Assert.Null(BrowserMcp.DevToolsRefusal(method));

    [Theory]
    [InlineData("Target.createTarget")]
    [InlineData("Browser.close")]
    [InlineData("Storage.getCookies")]
    [InlineData("SystemInfo.getInfo")]
    [InlineData("Network.getAllCookies")]
    [InlineData("network.GETALLCOOKIES")] // case does not slip past
    [InlineData("Network.getCookies")]
    [InlineData("Network.setCookie")]
    [InlineData("Network.setCookies")]
    [InlineData("Network.deleteCookies")]
    [InlineData("Network.clearBrowserCookies")]
    [InlineData("Network.loadNetworkResource")]
    [InlineData("DOM.setFileInputFiles")]
    [InlineData("Input.dispatchDragEvent")]
    [InlineData("Page.setDownloadBehavior")]
    [InlineData("Page.addScriptToEvaluateOnNewDocument")]
    [InlineData("Page.addScriptToEvaluateOnLoad")] // deprecated aliases
    [InlineData("Page.getCookies")]
    [InlineData("Page.deleteCookie")]
    [InlineData("Network.getAllCookies ")]
    [InlineData("Runtime")]
    [InlineData("Runtime.evaluate.x")]
    [InlineData("")]
    public void Devtools_refuses_other_domains_and_methods_that_reach_past_the_page(string method)
    {
        var why = BrowserMcp.DevToolsRefusal(method);
        Assert.NotNull(why);
        Assert.StartsWith($"Refused {method}: devtools reaches only the Accessibility,", why);
    }

    [Fact]
    public async Task Devtools_refusal_is_a_tool_error_and_Page_navigate_points_to_navigate()
    {
        Assert.Equal("Refused Page.navigate: use the navigate tool.", BrowserMcp.DevToolsRefusal("Page.navigate"));
        var r = Parse(await Mcp().HandleAsync(Call(1, "devtools", """{"method":"Network.getAllCookies"}""")))["result"]!;
        Assert.True(r["isError"]!.GetValue<bool>());
        Assert.StartsWith("Refused Network.getAllCookies: ", r["content"]![0]!["text"]!.GetValue<string>());
        Assert.Empty(_driver.Calls);
        var tools = Parse(await Mcp().HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""))["result"]!["tools"]!.AsArray();
        Assert.Contains("refuses cookie access", tools.Single(t => t!["name"]!.GetValue<string>() == "devtools")!["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task Turned_off_answers_every_call_without_the_driver()
    {
        _enabled = false;
        var r = Parse(await Mcp().HandleAsync(Call(1, "snapshot")))["result"]!;
        Assert.True(r["isError"]!.GetValue<bool>());
        Assert.Equal(BrowserMcp.Disabled, r["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("The Browser tool is turned off in CCWrapper Settings.", BrowserMcp.Disabled);
        Assert.Empty(_driver.Calls);
    }

    [Theory]
    [InlineData("{ nope", -32700)]
    [InlineData("[1,2]", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"resources/list"}""", -32601)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"fly","arguments":{}}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"navigate","arguments":{}}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"click","arguments":{"target":true}}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"snapshot","arguments":"x"}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"devtools","arguments":{"method":"A.b","params":[1]}}}""", -32602)]
    public async Task Bad_messages_get_json_rpc_errors(string line, int code)
    {
        var r = Parse(await Mcp().HandleAsync(line));
        Assert.Equal(code, r["error"]!["code"]!.GetValue<int>());
        Assert.Empty(_driver.Calls);
    }

    [Fact]
    public async Task Tool_calls_run_one_at_a_time()
    {
        _driver.Delay = TimeSpan.FromMilliseconds(50);
        var mcp = Mcp(); // one handler for every connection: its queue spans tabs
        var replies = await Task.WhenAll(Enumerable.Range(1, 4).Select(i => Task.Run(() => mcp.HandleAsync(Call(i, "snapshot")))));
        Assert.Equal(4, _driver.Calls.Count);
        Assert.Equal(1, _driver.MaxActive);
        Assert.Equal([1, 2, 3, 4], replies.Select(r => Parse(r)["id"]!.GetValue<int>()).Order());
    }

    static string TestPipe() => "CCWrapper.browser.test." + Guid.NewGuid().ToString("N");

    static async Task<(StreamWriter, StreamReader, NamedPipeClientStream)> ConnectAsync(string name)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(3000);
        return (new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" }, new StreamReader(client, Encoding.UTF8), client);
    }

    [Fact]
    public async Task Pipe_serves_two_clients_at_once_and_outlives_a_vanished_one()
    {
        var name = TestPipe();
        using var server = new BrowserPipe(name, Mcp());
        var (w1, r1, c1) = await ConnectAsync(name);
        var (w2, r2, c2) = await ConnectAsync(name);
        using (c2)
        {
            await w1.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await w1.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"ping"}""");
            await w2.WriteLineAsync(Call(2, "snapshot"));
            Assert.Equal("""{"jsonrpc":"2.0","id":1,"result":{}}""", await r1.ReadLineAsync());
            Assert.Equal("[1] button Go", Parse(await r2.ReadLineAsync())["result"]!["content"]![0]!["text"]!.GetValue<string>());

            c1.Dispose(); // client vanishes mid-session
            await w2.WriteLineAsync("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");
            Assert.Equal("""{"jsonrpc":"2.0","id":3,"result":{}}""", await r2.ReadLineAsync());
        }
        var (w3, r3, c3) = await ConnectAsync(name); // still accepting
        using (c3)
        {
            await w3.WriteLineAsync("""{"jsonrpc":"2.0","id":4,"method":"ping"}""");
            Assert.Equal("""{"jsonrpc":"2.0","id":4,"result":{}}""", await r3.ReadLineAsync());
        }
    }

    [Fact]
    public async Task Pipe_drops_a_connection_that_sends_a_line_over_1_MB()
    {
        var name = TestPipe();
        using var server = new BrowserPipe(name, Mcp());
        var (w, r, c) = await ConnectAsync(name);
        using (c)
        {
            try { await w.WriteAsync(new string('x', (1 << 20) + 10)); } catch (IOException) { } // the server may hang up mid-write
            Assert.Null(await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Pipe_stops_on_dispose()
    {
        var name = TestPipe();
        new BrowserPipe(name, Mcp()).Dispose();
        await Task.Delay(100);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        Assert.Throws<TimeoutException>(() => client.Connect(200));
    }

    [Fact]
    public async Task Relay_bridges_stdio_to_the_pipe()
    {
        var name = BrowserPipe.NameFor(Random.Shared.Next(1_000_000_000, int.MaxValue)); // digits only for the relay, but not our pid: ThemeLoadTests' MainWindow owns that name
        using var server = new BrowserPipe(name, Mcp());
        using var relay = StartRelay("--browser", name);
        try
        {
            await relay.StandardInput.WriteLineAsync(Call(9, "snapshot"));
            var reply = await relay.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(9, Parse(reply)["id"]!.GetValue<int>());
            relay.StandardInput.Close(); // Claude Code ends the server: the relay exits
            Assert.True(relay.WaitForExit(5000));
            Assert.Equal(0, relay.ExitCode);
        }
        finally { if (!relay.HasExited) relay.Kill(); }
    }

    [Theory]
    [InlineData("--browser", "other.pipe")]
    [InlineData("--browser", "CCWrapper.browser.")]
    [InlineData("--browser", "CCWrapper.browser.12x")]
    [InlineData("--browser", "CCWrapper.browser.999999999")] // nobody listens
    [InlineData("--browser")]
    public void Relay_refuses_a_bad_or_missing_pipe(params string[] args)
    {
        using var relay = StartRelay(args);
        Assert.True(relay.WaitForExit(20_000)); // nobody listening waits out the relay's 10 s connect
        Assert.Equal(1, relay.ExitCode);
        Assert.StartsWith("CCWrapper browser:", relay.StandardError.ReadToEnd());
    }

    static Process StartRelay(params string[] args) =>
        Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "CCWrapper.StatusRelay.exe"), args)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
        })!;
}
