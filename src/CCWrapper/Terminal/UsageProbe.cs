using System.ComponentModel;
using System.IO;
using CCWrapper.Core;
using CCWrapper.Data;

namespace CCWrapper.Terminal;

/// <summary>Refreshes an account's plan usage the way the user would: Claude Code's own <c>/usage</c>, run in a hidden
/// claude.exe (no window, no MCP servers), saves the result in .claude.json, where <see cref="AccountReader"/> reads it.
/// Nothing is read from the screen; the app still never calls Anthropic or reads credentials.</summary>
public static class UsageProbe
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30), MinGap = TimeSpan.FromMinutes(10), AfterFailure = TimeSpan.FromHours(1);
    static readonly Dictionary<string, Task<bool>> InFlight = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, DateTimeOffset> FailedAt = new(StringComparer.OrdinalIgnoreCase); // under the InFlight lock

    // Hooks off: a hidden check has no use for the user's SessionStart hooks, and each would start its own processes.
    const string NoHooks = """{"disableAllHooks":true}""";

    /// <summary>Why a check saved nothing, for the picker card and the bottom bar's tooltip.</summary>
    public const string FailedHint = "Claude Code did not save fresh usage for this account. Its login has probably expired: " +
        "run /login in a session of this account, then refresh.";

    /// <summary>Raised on a pool thread when an account's saved usage is fresh.</summary>
    public static event Action<Account>? Refreshed;

    /// <summary>True when the account's last check, within the hour, ended without fresh usage.</summary>
    public static bool FailedRecently(Account account)
    {
        lock (InFlight) return FailedAt.TryGetValue(account.Letter, out var at) && DateTimeOffset.Now - at < AfterFailure;
    }

    /// <summary>True when Claude Code saved fresh usage. One run per account at a time; unless <paramref name="force"/>d,
    /// skipped while the saved usage is younger than 10 minutes (each check adds a /usage line to that project's prompt history)
    /// and for an hour after a check that saved nothing (an expired login fails every time).</summary>
    public static Task<bool> RefreshAsync(Account account, bool force = false)
    {
        lock (InFlight)
        {
            if (InFlight.TryGetValue(account.Letter, out var running)) return running;
            var task = Task.Run(() => RunAsync(account, force));
            InFlight[account.Letter] = task;
            task.ContinueWith(_ => { lock (InFlight) InFlight.Remove(account.Letter); });
            return task;
        }
    }

    static async Task<bool> RunAsync(Account account, bool force)
    {
        var before = AccountReader.Read(account).UsageFetchedAt;
        if (!force && (before is { } at && DateTimeOffset.Now - at < MinGap || FailedRecently(account))) return false;
        var gone = new TaskCompletionSource();
        PtySession? pty = null;
        try
        {
            // The folder of the newest session: already trusted, so no trust question blocks /usage (none is ever answered).
            pty = await PtySession.StartAsync(ClaudeLaunch.FindClaude(), ["--strict-mcp-config", "--settings", NoHooks, "/usage"], Folder(account),
                ClaudeLaunch.Env(account, Guid.NewGuid(), Environment.GetEnvironmentVariables()), 120, 40,
                text => pty?.Ack(text.Length), _ => gone.TrySetResult());
            for (var deadline = DateTime.UtcNow + Timeout; DateTime.UtcNow < deadline && !gone.Task.IsCompleted;)
            {
                await Task.Delay(250);
                if (AccountReader.Read(account).UsageFetchedAt is { } now && now > (before ?? DateTimeOffset.MinValue))
                {
                    lock (InFlight) FailedAt.Remove(account.Letter);
                    Refreshed?.Invoke(account);
                    return true;
                }
            }
            // An expired login (/usage then shows nothing), a question on screen, or claude.exe exited: the saved usage stays
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException) { } // would not start
        finally { pty?.Dispose(); } // kills it; Claude Code drops its stale sessions\<pid>.json itself
        lock (InFlight) FailedAt[account.Letter] = DateTimeOffset.Now;
        return false;
    }

    static string Folder(Account account) =>
        SessionCatalog.List(account).Select(s => s.Cwd).FirstOrDefault(Directory.Exists)
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
