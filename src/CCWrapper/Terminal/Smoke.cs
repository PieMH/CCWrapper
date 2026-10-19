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
            return await EchoAsync() == 0 && await BrowserAsync() && await PanelAsync() ? 0 : 1;
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
            && list?["result"]?["tools"]?.AsArray().Count == 11
            && call?["id"]?.GetValue<int>() == 3
            && call?["result"]?["content"]?[0]?["text"]?.GetValue<string>() == "smoke-snapshot";
    }

    // The real Browser panel in a throwaway profile: snapshot refs, typing, clicking and evaluating on a data: page; then tabs:
    // a click's window.open that becomes a tab with its opener's window object, a timer's that is dropped, new_tab, switch_tab,
    // window.close() (pop-up tabs only) and close_tab.
    static async Task<bool> PanelAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccw-smoke-browser");
        try { Directory.Delete(dir, true); } catch (Exception) { } // the last run's profile; WebView2 still holds it until the process ends
        var panel = new CCWrapper.Views.BrowserPanel(dir);
        new Window
        {
            Content = panel, Width = 900, Height = 500, Left = -32000, Top = -32000, Title = "CCWrapper smoke browser",
            WindowStartupLocation = WindowStartupLocation.Manual, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false,
        }.Show();
        var run = PanelStepsAsync(panel);
        if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60))) == run && await run) return true;
        Console.Error.WriteLine("smoke: the Browser panel steps failed");
        return false;
    }

    static async Task<bool> PanelStepsAsync(IBrowserDriver panel)
    {
        const string html = "<h1>Smoke</h1><a href='#x'>link</a><input name=q placeholder=Q><button onclick=\"document.title='clicked:'+document.querySelector('input').value\">Go</button>"
            + "<button id=pop onclick=\"window.w=window.open('about:blank')\">Pop</button><button id=bad onclick=\"window.open('ftp://example.com/')\">Bad</button>";
        await panel.NavigateAsync("data:text/html," + Uri.EscapeDataString(html), default);
        var snap = await panel.SnapshotAsync(default);
        var button = System.Text.RegularExpressions.Regex.Match(snap, @"\[(\d+)\] button ""Go""");
        var input = System.Text.RegularExpressions.Regex.Match(snap, @"\[(\d+)\] input text name=""q""");
        if (!snap.Contains("# Smoke") || !snap.Contains("link \"link\" -> ") || !button.Success || !input.Success) return false;
        await panel.TypeAsync(input.Groups[1].Value, "hello", false, default);
        await panel.ClickAsync(button.Groups[1].Value, default);
        var title = await panel.EvaluateAsync("document.title", default);
        try { await panel.ClickAsync("999", default); return false; }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("No element matches")) { }
        if (title != "\"clicked:hello\"") return false;
        return await TabStepsAsync(panel);
    }

    static async Task<bool> TabStepsAsync(IBrowserDriver panel)
    {
        if (!(await panel.ClickAsync("#pop", default)).Contains("Tab 2 is now in front") || !(await panel.TabsAsync(default)).Contains("Tab 2 (front): ")) return false;
        await panel.SwitchTabAsync(1, default);
        if (await panel.EvaluateAsync("!!window.w && !window.w.closed", default) != "true") return false; // the popup tab kept its opener link
        var dropped = await panel.ClickAsync("#bad", default); // an ftp: popup is refused (a file: one never reaches NewWindowRequested): no new tab
        var listed = await panel.TabsAsync(default);
        if (dropped.Contains("is now in front") || !listed.Contains("Tab 2") || listed.Contains("Tab 3")) return false;
        // a page that opens a window on its own timer, with no click: dropped, so no tab 4 and the front stays
        var opened = await panel.NewTabAsync("data:text/html," + Uri.EscapeDataString("<title>Two</title><h1>Second</h1><script>setTimeout(() => window.open('https://example.com/'), 100)</script>"), default);
        if (!opened.StartsWith("Opened tab 3 in front. Loaded") || await panel.EvaluateAsync("document.title", default) != "\"Two\"") return false;
        await Task.Delay(1000);
        if ((await panel.TabsAsync(default)).Contains("Tab 4")) return false;
        await panel.SwitchTabAsync(1, default);
        await panel.SwitchTabAsync(3, default);
        var tabs = await panel.TabsAsync(default);
        if (!tabs.Contains("Tab 1: clicked:hello - data:") || !tabs.Contains("Tab 3 (front): Two - data:")) return false;
        // window.close() leaves a tab Claude or the user opened (one history entry, which Chromium lets a script close) and closes a pop-up tab
        await panel.EvaluateAsync("setTimeout(() => window.close(), 300)", default);
        await Task.Delay(1000);
        if (!(await panel.TabsAsync(default)).Contains("Tab 3 (front)")) return false;
        await panel.SwitchTabAsync(2, default);
        await panel.EvaluateAsync("setTimeout(() => window.close(), 300)", default);
        await Task.Delay(1000);
        var afterPopup = await panel.TabsAsync(default);
        if (afterPopup.Contains("Tab 2") || !afterPopup.Contains("Tab 1 (front)")) return false; // tab 3 is its strip neighbour, but tab 1 opened it
        await panel.CloseTabAsync(3, default);
        var left = await panel.TabsAsync(default);
        if (left.Contains("Tab 2") || left.Contains("Tab 3") || !left.Contains("Tab 1 (front)")) return false;
        try { await panel.SwitchTabAsync(3, default); return false; }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("There is no tab 3")) { }
        return (await panel.SnapshotAsync(default)).Contains("# Smoke");
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
        public Task<string> TabsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> NewTabAsync(string url, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> SwitchTabAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> CloseTabAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    }
}
