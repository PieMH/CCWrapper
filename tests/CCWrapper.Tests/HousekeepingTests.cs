using System.IO;
using CCWrapper.Core;
using CCWrapper.Terminal;

namespace CCWrapper.Tests;

public sealed class HousekeepingTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ccw-house-").FullName;
    static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Make(string name, int daysOld)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, Now.AddDays(-daysOld));
        return path;
    }

    [Fact]
    public void Deletes_only_old_files_of_the_pattern()
    {
        var old = Make("paste-1.png", 8);
        var fresh = Make("paste-2.png", 6);
        var other = Make("notes.png", 30);
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "paste-dir.png")).FullName;

        var n = Housekeeping.DeleteOlderThan(_dir, PasteHandler.FilePattern, Housekeeping.PasteMaxAge, Now);

        Assert.Equal(1, n);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(other)); // not the app's own naming
        Assert.True(Directory.Exists(sub));
    }

    [Fact]
    public void A_file_in_use_stays_and_a_missing_folder_is_fine()
    {
        var held = Make("paste-3.png", 9);
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(0, Housekeeping.DeleteOlderThan(_dir, PasteHandler.FilePattern, Housekeeping.PasteMaxAge, Now));
        Assert.Equal(0, Housekeeping.DeleteOlderThan(Path.Combine(_dir, "nope"), "*.json", Housekeeping.LiveMaxAge, Now));
    }

    [Fact]
    public void Feed_files_survive_until_a_day_old()
    {
        var orphan = Make("a.json", 2);
        var fresh = Make("b.json", 0);
        Housekeeping.DeleteOlderThan(_dir, "*.json", Housekeeping.LiveMaxAge, Now);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void CacheBytes_counts_the_http_and_code_caches_only()
    {
        Assert.Equal(0, Housekeeping.CacheBytes(_dir));
        var def = Path.Combine(_dir, "EBWebView", "Default");
        Directory.CreateDirectory(Path.Combine(def, "Cache", "Cache_Data"));
        Directory.CreateDirectory(Path.Combine(def, "Code Cache"));
        Directory.CreateDirectory(Path.Combine(def, "Local Storage"));
        File.WriteAllBytes(Path.Combine(def, "Cache", "Cache_Data", "f"), new byte[100]);
        File.WriteAllBytes(Path.Combine(def, "Code Cache", "g"), new byte[10]);
        File.WriteAllBytes(Path.Combine(def, "Local Storage", "h"), new byte[1000]);
        Assert.Equal(110, Housekeeping.CacheBytes(_dir));
    }
}
