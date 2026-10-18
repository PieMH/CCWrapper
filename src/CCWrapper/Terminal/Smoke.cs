using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using CCWrapper.Core;

namespace CCWrapper.Terminal;

/// <summary><c>CCWrapper.exe --smoke</c>: round-trips <c>cmd /c echo</c> through ConPTY into xterm.js, and reads it back plain and styled;
/// then talks MCP to a <see cref="BrowserPipe"/> through the relay exe in <c>--browser</c> mode, as a Claude Code tab does.</summary>
public static class Smoke
{
    /// <summary>Returns the process exit code: 0 on success.</summary>
    public static async Task<int> RunAsync()
    {
        try
        {
            return await EchoAsync() == 0 && await BrowserAsync() ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    static async Task<int> EchoAsync()
    {
        var host = new TerminalHost();
        // Off-screen instead of hidden: WebView2 only initializes inside a shown window. It is never closed here:
        // closing the last window would end the app with code 0 before App passes ours.
        new Window
        {
            Content = host, Width = 900, Height = 500, Left = -32000, Top = -32000, Title = "CCWrapper smoke",
            WindowStartupLocation = WindowStartupLocation.Manual, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false,
        }.Show();
        var session = new LiveSession(new Account("smoke", null, "smoke.json", "Smoke"));
        var run = EchoedAsync(host, session);
        return await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20))) == run && await run ? 0 : 1;
    }

    // The dim word must come back marked in a styled read: that is how TuiInput tells Claude Code's greyed placeholder.
    static async Task<bool> EchoedAsync(TerminalHost host, LiveSession session)
    {
        await host.OpenProcessAsync(session, Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "echo", "ccw-smoke", "\x1b[2mdim\x1b[22m"], Environment.SystemDirectory);
        while (!((await host.ReadTextAsync(session))?.Split('\n').Any(line => line.Trim() == "ccw-smoke dim") ?? false))
            await Task.Delay(100);
        return (await host.ReadTailAsync(session, 5, styled: true))?.Contains("ccw-smoke \x1b[2mdim") == true;
    }

    static async Task<bool> BrowserAsync()
    {
        var name = BrowserPipe.NameFor(Environment.ProcessId);
        using var pipe = new BrowserPipe(name, new BrowserMcp(new SmokeDriver(), () => true));
        using var relay = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "CCWrapper.StatusRelay.exe"), ["--browser", name])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        })!;
        try
        {
            var talk = TalkAsync(relay);
            if (await Task.WhenAny(talk, Task.Delay(TimeSpan.FromSeconds(10))) == talk && await talk) return true;
            Console.Error.WriteLine("smoke: the Browser MCP round trip through the relay failed");
            return false;
        }
        finally { try { relay.Kill(); } catch (Exception) { } }
    }

    static async Task<bool> TalkAsync(Process relay)
    {
        async Task<JsonNode?> Ask(string request)
        {
            await relay.StandardInput.WriteLineAsync(request);
            return JsonNode.Parse(await relay.StandardOutput.ReadLineAsync() ?? "null");
        }
        var init = await Ask("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}""");
        await relay.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        var list = await Ask("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var call = await Ask("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"snapshot","arguments":{}}}""");
        return init?["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "ccwrapper-browser"
            && list?["result"]?["tools"]?.AsArray().Count == 7
            && call?["id"]?.GetValue<int>() == 3
            && call?["result"]?["content"]?[0]?["text"]?.GetValue<string>() == "smoke-snapshot";
    }

    sealed class SmokeDriver : IBrowserDriver
    {
        public Task<string> SnapshotAsync(CancellationToken ct) => Task.FromResult("smoke-snapshot");
        public Task<string> NavigateAsync(string url, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> ClickAsync(string target, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> TypeAsync(string target, string text, bool submit, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> ScreenshotAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> EvaluateAsync(string expression, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> DevToolsAsync(string method, string paramsJson, CancellationToken ct) => throw new NotSupportedException();
    }
}
