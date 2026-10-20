using System.IO;
using CCWrapper.Data;
using CCWrapper.Terminal;

namespace CCWrapper.Core;

/// <summary>Cleans up what the app itself wrote under <c>%LOCALAPPDATA%\CCWrapper</c>, by age. Best effort: nothing here throws,
/// and nothing outside the folder it is given is touched. Not touched at all: the terminal profiles, backups, app.json.</summary>
public static class Housekeeping
{
    public static readonly TimeSpan PasteMaxAge = TimeSpan.FromDays(7);
    /// <summary>What a crash left behind goes after this; a file another window (account) owns is only deleted once it was not written for this long.</summary>
    public static readonly TimeSpan LiveMaxAge = TimeSpan.FromDays(1);
    public const long CacheLimit = 100L << 20;

    /// <summary>Once at app start, off the UI thread.</summary>
    public static void Run()
    {
        var now = DateTime.UtcNow;
        DeleteOlderThan(Path.Combine(ClaudeLaunch.AppDataDir, "paste"), PasteHandler.FilePattern, PasteMaxAge, now);
        DeleteOlderThan(StatusLineFeed.LiveDir, "*.json", LiveMaxAge, now);
        try { Directory.Delete(Path.Combine(ClaudeLaunch.AppDataDir, "WebView2", "smoke"), recursive: true); } catch (Exception) { } // --smoke's terminal profile; still held while a smoke run is going
    }

    /// <summary>Deletes the files of <paramref name="dir"/> (not its subfolders) matching <paramref name="pattern"/> that were last written more than
    /// <paramref name="age"/> before <paramref name="now"/>. Returns how many went.</summary>
    public static int DeleteOlderThan(string dir, string pattern, TimeSpan age, DateTime now)
    {
        var deleted = 0;
        try
        {
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles(pattern))
            {
                try
                {
                    if (now - file.LastWriteTimeUtc <= age) continue;
                    file.Delete();
                    deleted++;
                }
                catch (Exception) { } // in use or gone: the next start tries again
            }
        }
        catch (Exception) { } // no such folder
        return deleted;
    }

    /// <summary>Size of the WebView2 profile's HTTP and code caches (what <c>ClearBrowsingDataAsync(DiskCache)</c> clears); 0 when there are none.</summary>
    public static long CacheBytes(string profile)
    {
        long total = 0;
        foreach (var name in new[] { "Cache", "Code Cache" })
        {
            try
            {
                total += new DirectoryInfo(Path.Combine(profile, "EBWebView", "Default", name))
                    .EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception) { }
        }
        return total;
    }
}
