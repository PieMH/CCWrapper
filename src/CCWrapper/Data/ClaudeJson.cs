using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace CCWrapper.Data;

/// <summary>Changes .claude.json the way Claude Code does. Every running claude rewrites this file, each time under a lock
/// (a "&lt;file&gt;.lock" folder, made by proper-lockfile) and on a fresh read of it. So a change is made while holding that
/// same lock, on the file as it is then, never on a copy read earlier.</summary>
public static class ClaudeJson
{
    static readonly TimeSpan Stale = TimeSpan.FromSeconds(10); // proper-lockfile's default, which Claude Code keeps
    const int AlreadyExists = 183;

    /// <summary>Null when written or when nothing changed, else why not. <paramref name="change"/> edits the file's object
    /// and returns null, or returns why it can't (nothing is written then). The old file is backed up first.</summary>
    public static string? Update(string path, Func<JsonObject, string?> change, string backupDir)
    {
        var lockDir = path + ".lock";
        if (!Lock(lockDir)) return "Claude Code is writing its config file right now. Try again in a moment.";
        try
        {
            if (SafeFile.Read(path) is not { } text) return "Claude Code's config file is missing.";
            if (SettingsFile.Parse(text, out var root) is { } error) return "Claude Code's config file is not valid JSON: " + error;
            var before = root!.DeepClone();
            if (change(root) is { } why) return why;
            return JsonNode.DeepEquals(before, root) ? null : SafeFile.Write(path, text, SettingsFile.Format(root, text), backupDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        finally
        {
            try { Directory.Delete(lockDir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Makes the lock folder, waiting up to 2 s while it exists. One nobody touched for 10 s was left by a claude
    /// that ended while holding it: it is taken over, as Claude Code does.</summary>
    static bool Lock(string lockDir)
    {
        for (var i = 0; i < 20; i++)
        {
            if (CreateDirectoryW(lockDir, IntPtr.Zero)) return true;
            if (Marshal.GetLastWin32Error() != AlreadyExists) return false;
            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(lockDir) > Stale)
                {
                    Directory.Delete(lockDir);
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Thread.Sleep(100);
        }
        return false;
    }

    // Directory.CreateDirectory succeeds when the folder exists: taking a lock needs a create that fails then.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateDirectoryW(string path, IntPtr security);
}
