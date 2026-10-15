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
        // One stray exception must not take every session down with the process (job objects).
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, t) => t.SetObserved();
        var i = Array.FindIndex(e.Args, a => a.Equals("--account", StringComparison.OrdinalIgnoreCase));
        var account = i >= 0 && i + 1 < e.Args.Length ? AccountStore.Find(e.Args[i + 1]) : null;
        if (account is null) new AccountPicker().Show();
        else new MainWindow(account).Show();
    }

    bool _showingError;

    // Shows the error once: a timer that keeps failing must not stack boxes while one is open.
    void OnUnhandled(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_showingError) return;
        _showingError = true;
        try { MessageBox.Show($"{e.Exception.GetType().Name}: {e.Exception.Message}\n\nCCWrapper keeps running.", "CCWrapper: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _showingError = false; }
    }

    /// <summary>Relaunches this exe with <c>--account x</c> and exits; this window's sessions end and stay resumable.
    /// Callers confirm first when a session is busy.</summary>
    public static void RestartAsAccount(Account account)
    {
        Process.Start(Environment.ProcessPath!, ["--account", account.Letter]);
        Current.Shutdown();
    }
}
