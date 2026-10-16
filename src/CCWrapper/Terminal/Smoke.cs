using System.IO;
using System.Windows;
using CCWrapper.Core;

namespace CCWrapper.Terminal;

/// <summary><c>CCWrapper.exe --smoke</c>: round-trips <c>cmd /c echo</c> through ConPTY into xterm.js, and reads it back plain and styled.</summary>
public static class Smoke
{
    /// <summary>Returns the process exit code: 0 on success.</summary>
    public static async Task<int> RunAsync()
    {
        try
        {
            return await EchoAsync();
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
}
