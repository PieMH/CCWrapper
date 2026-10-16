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
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30), MinGap = TimeSpan.FromMinutes(10);
    static readonly Dictionary<string, Task<bool>> InFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised on a pool thread when an account's saved usage is fresh.</summary>
    public static event Action<Account>? Refreshed;

    /// <summary>True when Claude Code saved fresh usage. One run per account at a time; unless <paramref name="force"/>d,
    /// skipped while the saved usage is younger than 10 minutes (each check adds a /usage line to that project's prompt history).</summary>
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
        if (!force && before is { } at && DateTimeOffset.Now - at < MinGap) return false;
        var gone = new TaskCompletionSource();
        PtySession? pty = null;
        try
        {
            // The folder of the newest session: already trusted, so no trust question blocks /usage (none is ever answered).
            pty = await PtySession.StartAsync(ClaudeLaunch.FindClaude(), ["--strict-mcp-config", "/usage"], Folder(account),
                ClaudeLaunch.Env(account, Guid.NewGuid(), Environment.GetEnvironmentVariables()), 120, 40,
                text => pty?.Ack(text.Length), _ => gone.TrySetResult());
            for (var deadline = DateTime.UtcNow + Timeout; DateTime.UtcNow < deadline && !gone.Task.IsCompleted;)
            {
                await Task.Delay(250);
                if (AccountReader.Read(account).UsageFetchedAt is { } now && now > (before ?? DateTimeOffset.MinValue))
                {
                    Refreshed?.Invoke(account);
                    return true;
                }
            }
            return false; // not logged in, a question on screen, or claude.exe exited: the saved usage stays
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException) { return false; } // would not start
        finally { pty?.Dispose(); } // kills it; Claude Code drops its stale sessions\<pid>.json itself
    }

    static string Folder(Account account) =>
        SessionCatalog.List(account).Select(s => s.Cwd).FirstOrDefault(Directory.Exists)
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
