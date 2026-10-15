using System.IO;
using System.Windows;
using CCWrapper.Core;

namespace CCWrapper.Terminal;

/// <summary><c>CCWrapper.exe --smoke</c>: round-trips <c>cmd /c echo</c> through ConPTY into xterm.js.
/// <c>--smoke --smoke-claude &lt;letter&gt;</c>: a visible window with one real claude.exe session of that account,
/// started in the current directory, for manual checks.</summary>
public static class Smoke
{
    /// <summary>Returns the process exit code: 0 on success.</summary>
    public static async Task<int> RunAsync()
    {
        try
        {
            var argv = Environment.GetCommandLineArgs();
            var i = Array.FindIndex(argv, a => a.Equals("--smoke-claude", StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? await ClaudeAsync(i + 1 < argv.Length ? argv[i + 1] : "c") : await EchoAsync();
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

    static async Task<bool> EchoedAsync(TerminalHost host, LiveSession session)
    {
        await host.OpenProcessAsync(session, Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "echo", "ccw-smoke"], Environment.SystemDirectory);
        while (!((await host.ReadTextAsync(session))?.Split('\n').Any(line => line.Trim() == "ccw-smoke") ?? false))
            await Task.Delay(100);
        return true;
    }

    static async Task<int> ClaudeAsync(string letter)
    {
        if (AccountStore.Find(letter) is not { } account) return 1;
        var host = new TerminalHost();
        var window = new Window { Content = host, Width = 1200, Height = 800, Title = $"CCWrapper smoke · claude {account.Letter.ToUpperInvariant()}" };
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        host.CloseRequested += (_, _) => window.Close();
        window.Show();
        await host.OpenAsync(new LiveSession(account), new LaunchOptions(Environment.CurrentDirectory));
        await closed.Task;
        return 0;
    }
}
