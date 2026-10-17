using System.Diagnostics;
using System.IO;
using System.Reflection;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace CCWrapper.Core;

/// <summary>Updates from GitHub Releases through Velopack. Installed copies only, and only with the user's yes
/// (<see cref="AppPrefs"/>): a dev build or an answer of "no" makes no network call. The one request sends the IP address and
/// the app version to GitHub (see PRIVACY.md). Called on the UI thread; failures are quiet.</summary>
public static class AppUpdate
{
    public const string Repo = "https://github.com/PieMH/CCWrapper";

    /// <summary>Raised when <see cref="ReadyVersion"/> changes.</summary>
    public static event Action? Changed;

    /// <summary>The downloaded version waiting for a restart, or null.</summary>
    public static string? ReadyVersion => _ready?.Version.ToString();

    /// <summary>This build's version: the csproj <c>Version</c>.</summary>
    public static string Version { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static bool IsInstalled => VelopackLocator.IsCurrentSet && VelopackLocator.Current.CurrentlyInstalledVersion is not null;

    static UpdateManager? _mgr;
    static VelopackAsset? _ready;
    static bool _checking;
    static CancellationTokenSource? _cts;

    /// <summary>A prerelease build looks at GitHub prereleases too; a stable one never moves to one.</summary>
    public static bool UsePrerelease(string version) => SemanticVersion.Parse(version).IsPrerelease;

    /// <summary>The arguments that reopen the window of <paramref name="accountLetter"/> after an update (as <c>App.RestartAsAccount</c>
    /// starts it); none without an account, i.e. the picker.</summary>
    public static string[] RestartArgs(string? accountLetter) => accountLetter is null ? [] : ["--account", accountLetter];

    /// <summary>The updater force-stops every process running from the install folder, so it may only start when this is the last
    /// CCWrapper there.</summary>
    public static bool MayHandOff(int otherInstances) => otherInstances == 0;

    /// <summary>CCWrapper processes other than <paramref name="selfPid"/> that run from <paramref name="installRoot"/>. A path
    /// that could not be read counts: failing to count means not handing off.</summary>
    public static int OtherInstances(IEnumerable<(int Pid, string? Path)> processes, int selfPid, string installRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(installRoot) + Path.DirectorySeparatorChar;
        return processes.Count(p => p.Pid != selfPid && (p.Path is null || p.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Other CCWrapper windows of this install (one process per account window).</summary>
    public static int OtherInstances()
    {
        if (!IsInstalled) return 0;
        var procs = Process.GetProcessesByName("CCWrapper").Select(p => { using (p) return (p.Id, Path: TryPath(p)); }).ToList();
        return OtherInstances(procs, Environment.ProcessId, VelopackLocator.Current.RootAppDir!);
    }

    static string? TryPath(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch (Exception) { return null; } // access denied, or it exited meanwhile
    }

    /// <summary>At startup. <paramref name="askFirstRun"/> runs when the question was never answered, before any request.</summary>
    public static async Task StartAsync(Func<bool> askFirstRun)
    {
        if (!IsInstalled) return;
        var on = AppPrefs.ReadUpdateCheck();
        if (on is null)
        {
            on = askFirstRun();
            AppPrefs.WriteUpdateCheck(on.Value);
        }
        if (on == true) await CheckAsync();
    }

    /// <summary>The About window's switch: applies at once. Off also drops a downloaded update.</summary>
    public static void SetEnabled(bool on)
    {
        AppPrefs.WriteUpdateCheck(on);
        if (!on)
        {
            _cts?.Cancel();
            if (_ready is not null)
            {
                _ready = null;
                Changed?.Invoke();
            }
        }
        else if (on && IsInstalled && _ready is null) _ = CheckAsync();
    }

    static async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            _mgr ??= new UpdateManager(new GithubSource(Repo, null, UsePrerelease(VelopackLocator.Current.CurrentlyInstalledVersion!.ToString())));
            var asset = _mgr.UpdatePendingRestart; // downloaded by an earlier run
            if (asset is null)
            {
                var info = await _mgr.CheckForUpdatesAsync(); // 1.2.161: no token here, the guard below covers it
                if (info is null || ct.IsCancellationRequested) return;
                await _mgr.DownloadUpdatesAsync(info, cancelToken: ct);
                asset = info.TargetFullRelease;
            }
            if (ct.IsCancellationRequested || AppPrefs.ReadUpdateCheck() != true) return; // switched off meanwhile
            _ready = asset;
            Changed?.Invoke();
        }
        catch (Exception) { } // offline, rate-limited, no release yet: nothing to tell the user
        finally { _checking = false; }
    }

    /// <summary>Exits at once, installs and starts the new version in <paramref name="accountLetter"/>'s window. Callers confirmed first.</summary>
    public static void RestartNow(string? accountLetter)
    {
        if (_ready is not null && AppPrefs.ReadUpdateCheck() == true) _mgr?.ApplyUpdatesAndRestart(_ready, RestartArgs(accountLetter));
    }

    /// <summary>On exit: hands a downloaded update to the updater, unless another window of this install still runs.</summary>
    public static void ApplyOnExit()
    {
        try
        {
            if (_ready is not null && AppPrefs.ReadUpdateCheck() == true && MayHandOff(OtherInstances()))
                _mgr?.WaitExitThenApplyUpdates(_ready, silent: true, restart: false);
        }
        catch (Exception) { }
    }
}
