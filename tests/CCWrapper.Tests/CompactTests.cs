using System.IO;
using System.Text.Json.Nodes;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Views;

namespace CCWrapper.Tests;

public sealed class CompactTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ccw-app-").FullName;
    string Path => System.IO.Path.Combine(_dir, "app.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_or_broken_file_reads_as_defaults()
    {
        Assert.Equal(CompactPrefs.Default, CompactPrefs.Load(Path));
        File.WriteAllText(Path, "{ not json");
        Assert.Equal(CompactPrefs.Default, CompactPrefs.Load(Path));
        File.WriteAllText(Path, """{"compact":{"enabled":"yes","threshold":150,"prompt":"  "}}""");
        Assert.Equal(CompactPrefs.Default, CompactPrefs.Load(Path)); // each bad value falls back on its own
    }

    [Fact]
    public void Save_round_trips_and_keeps_foreign_keys()
    {
        File.WriteAllText(Path, """{"updateCheck":{"last":"2026-10-01"}}""");
        var prefs = new CompactPrefs(false, 70, "Keep goals.");

        Assert.Null(prefs.Save(Path));

        Assert.Equal(prefs, CompactPrefs.Load(Path));
        Assert.Equal("2026-10-01", JsonNode.Parse(File.ReadAllText(Path))!["updateCheck"]!["last"]!.GetValue<string>());
    }

    [Fact]
    public void Save_creates_the_file_and_refuses_to_overwrite_a_broken_one()
    {
        Assert.Null(CompactPrefs.Default.Save(Path));
        Assert.True(File.Exists(Path));

        File.WriteAllText(Path, "{ not json");
        Assert.NotNull(CompactPrefs.Default.Save(Path));
        Assert.Equal("{ not json", File.ReadAllText(Path));
    }

    [Fact]
    public void Command_is_one_line()
    {
        Assert.Equal("/compact Keep a. b", new CompactPrefs(true, 50, "Keep a.\r\nb").Command);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(99, true)]
    [InlineData(100, false)]
    public void Threshold_range(int t, bool ok) => Assert.Equal(ok, CompactPrefs.ValidThreshold(t));

    static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Local)); // local noon: +2h stays today

    [Fact]
    public void Usage_tip_says_when_each_window_resets()
    {
        var five = Now.AddHours(2).AddMinutes(10);
        var week = Now.AddDays(3).AddHours(4).AddMinutes(30);
        var sl = new StatusLineData { FiveHourUsedPercent = 58, SevenDayUsedPercent = 9, FiveHourResetsAt = five, SevenDayResetsAt = week };

        var tip = BarFormat.Usage(sl, null, Now).Tip;

        Assert.Contains($"5h window resets {five.ToLocalTime():HH:mm} (in 2h 10m)", tip);
        Assert.Contains($"Weekly window resets {week.ToLocalTime().ToString("ddd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} (in 3d 4h)", tip);
        Assert.Contains("status line", tip);
    }

    [Fact]
    public void Usage_tip_takes_resets_from_the_same_source_as_the_percent()
    {
        var saved = Now.AddHours(-1);
        var cached = new AccountInfo(null, null, null, null, null, saved,
            new UsageWindow(81, Now.AddMinutes(45)), new UsageWindow(12, null), null, null, null);
        var live = new StatusLineData { FiveHourUsedPercent = 58, FiveHourResetsAt = Now.AddHours(4), ReceivedAt = Now };

        Assert.Contains("(in 4h 0m)", BarFormat.Usage(live, cached, Now).Tip);
        var fromSaved = BarFormat.Usage(new(), cached, Now).Tip;
        Assert.Contains("(in 45m)", fromSaved);
        Assert.DoesNotContain("Weekly", fromSaved); // unknown: no line
    }

    [Fact]
    public void Usage_tip_marks_a_passed_reset_and_omits_unknown_ones()
    {
        var sl = new StatusLineData { FiveHourUsedPercent = 58, FiveHourResetsAt = Now.AddMinutes(-5) };
        Assert.Contains("5h window reset was due", BarFormat.Usage(sl, null, Now).Tip);
        Assert.DoesNotContain("resets", BarFormat.Usage(null, null, Now).Tip);
    }
}
