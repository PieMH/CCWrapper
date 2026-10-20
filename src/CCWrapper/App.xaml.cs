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
        await AppUpdate.StartAsync(AskUpdateCheck);
    }

    /// <summary>Asked once in an installed copy, before the first request to GitHub.</summary>
    bool AskUpdateCheck() => Dialog.Show(Windows.OfType<Window>().FirstOrDefault(),
        "CCWrapper can check GitHub for new versions. Your IP address and the app version reach GitHub when it does; nothing else is sent.\n\n" +
        $"Details: {AppUpdate.Repo}/blob/main/PRIVACY.md\nYou can change this later under About.\n\nCheck for updates?",
        "CCWrapper: updates", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    protected override void OnExit(ExitEventArgs e)
    {
        DraftStore.SaveAll();
        AppUpdate.ApplyOnExit();
        base.OnExit(e);
    }

    bool _showingError;

    // Shows the error once: a timer that keeps failing must not stack boxes while one is open.
    // With no window left (the first one threw while opening) it would run on invisibly: show the error and exit instead.
    void OnUnhandled(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        var windowOpen = Windows.OfType<Window>().Any(w => w.IsVisible);
        e.Handled = true;
        if (_showingError) return;
        _showingError = true;
        var text = $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n{(windowOpen ? "CCWrapper keeps running." : "CCWrapper will close.")}";
        try { Dialog.Show(null, text, "CCWrapper: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error); }
        catch (Exception) { MessageBox.Show(text, "CCWrapper: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error); } // the themed window failed too
        finally { _showingError = false; }
        if (!windowOpen) Shutdown(1);
    }

    /// <summary>Relaunches this exe with <c>--account x</c> and exits; this window's sessions end and stay resumable.
    /// Callers confirm first when a session is busy.</summary>
    public static void RestartAsAccount(Account account)
    {
        Process.Start(Environment.ProcessPath!, ["--account", account.Letter]);
        Current.Shutdown();
    }
}
