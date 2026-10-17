using System.IO;
using CCWrapper.Core;

namespace CCWrapper.Tests.Shell;

public sealed class UpdateTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ccw-update-" + Guid.NewGuid().ToString("N"));
    string File_ => Path.Combine(_dir, "app.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Prefs_round_trip_and_keep_other_keys()
    {
        AppPrefs.WriteUpdateCheck(true, File_);
        Assert.True(AppPrefs.ReadUpdateCheck(File_));
        File.WriteAllText(File_, """{"other":1,"updateCheck":true}""");
        AppPrefs.WriteUpdateCheck(false, File_);
        Assert.False(AppPrefs.ReadUpdateCheck(File_));
        Assert.Contains("\"other\":1", File.ReadAllText(File_));
    }

    [Theory]
    [InlineData(null)]               // missing file
    [InlineData("{ nope")]           // broken JSON
    [InlineData("[]")]               // not an object
    [InlineData("""{"updateCheck":"yes"}""")] // wrong type
    public void Prefs_unreadable_means_never_asked(string? content)
    {
        if (content is not null) { Directory.CreateDirectory(_dir); File.WriteAllText(File_, content); }
        Assert.Null(AppPrefs.ReadUpdateCheck(File_));
    }

    [Fact]
    public void Prefs_write_replaces_a_broken_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{ nope");
        AppPrefs.WriteUpdateCheck(true, File_);
        Assert.True(AppPrefs.ReadUpdateCheck(File_));
    }

    [Theory]
    [InlineData("0.1.0", false)]
    [InlineData("1.2.3", false)]
    [InlineData("0.2.0-beta.1", true)]
    public void Only_prerelease_builds_follow_prereleases(string version, bool expected) =>
        Assert.Equal(expected, AppUpdate.UsePrerelease(version));

    [Fact]
    public void Updater_starts_only_when_no_other_window_of_the_install_runs()
    {
        const string root = @"C:\Users\x\AppData\Local\PieMH.CCWrapper";
        (int, string?)[] procs =
        [
            (10, root + @"\current\CCWrapper.exe"),      // this one
            (11, @"D:\dev\CCWrapper\bin\CCWrapper.exe"), // another folder: not touched by the updater
        ];
        Assert.Equal(0, AppUpdate.OtherInstances(procs, 10, root));
        Assert.True(AppUpdate.MayHandOff(0));

        var withOther = procs.Append((12, root.ToLowerInvariant() + @"\current\ccwrapper.exe"));
        Assert.Equal(1, AppUpdate.OtherInstances(withOther, 10, root + @"\"));
        Assert.False(AppUpdate.MayHandOff(1));

        Assert.Equal(1, AppUpdate.OtherInstances([(10, root), (13, null)], 10, root)); // unreadable path: assume it is ours
    }

    [Fact]
    public void Restart_reopens_the_same_account_window()
    {
        Assert.Equal(["--account", "b"], AppUpdate.RestartArgs("b"));
        Assert.Empty(AppUpdate.RestartArgs(null)); // the picker
    }
}
