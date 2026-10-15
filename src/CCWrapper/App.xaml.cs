using System.Diagnostics;
using System.Windows;
using CCWrapper.Core;
using CCWrapper.Terminal;
using CCWrapper.Views;

namespace CCWrapper;

public partial class App : Application
{
    /// <summary><c>--smoke</c>: terminal self-test; <c>--account x</c>: main window as x; otherwise the picker.</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(await Smoke.RunAsync());
            return;
        }
        var i = Array.FindIndex(e.Args, a => a.Equals("--account", StringComparison.OrdinalIgnoreCase));
        var account = i >= 0 && i + 1 < e.Args.Length ? AccountStore.Find(e.Args[i + 1]) : null;
        if (account is null) new AccountPicker().Show();
        else new MainWindow(account).Show();
    }

    /// <summary>Relaunches this exe with <c>--account x</c> and exits; this window's sessions end and stay resumable.
    /// Callers confirm first when a session is busy.</summary>
    public static void RestartAsAccount(Account account)
    {
        Process.Start(Environment.ProcessPath!, ["--account", account.Letter]);
        Current.Shutdown();
    }
}
