using System.IO;
using CCWrapper.Data;
using CCWrapper.Views;

namespace CCWrapper.Tests.Data;

public sealed class UsageStatsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ccw-usage-").FullName;
    static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static string Rec(DateTimeOffset at, string? model, string? id, int input = 0, int output = 0, int write = 0, int read = 0, bool usage = true)
    {
        var m = (model is null ? "" : $"\"model\":\"{model}\",") + (id is null ? "" : $"\"id\":\"{id}\",")
            + (usage ? $"\"usage\":{{\"input_tokens\":{input},\"output_tokens\":{output},\"cache_creation_input_tokens\":{write},\"cache_read_input_tokens\":{read}}}," : "");
        return $"{{\"type\":\"assistant\",\"message\":{{{m}\"role\":\"assistant\"}},\"timestamp\":\"{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}\"}}";
    }

    string Write(string rel, DateTime? mtimeUtc, params string[] lines)
    {
        var path = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        File.SetLastWriteTimeUtc(path, mtimeUtc ?? Now.UtcDateTime);
        return path;
    }

    UsageStatsResult Run() => UsageStats.Compute(_dir, Now.AddHours(-5), Now.AddDays(-7));

    [Fact]
    public void Sums_each_window_and_splits_tokens_by_model()
    {
        Write(@"p\s.jsonl", null,
            Rec(Now.AddHours(-1), "claude-opus-5-5", "m1", input: 10, output: 20, write: 30, read: 1000),
            Rec(Now.AddHours(-2), "claude-sonnet-5", "m2", output: 40),
            Rec(Now.AddHours(-6), "claude-opus-5-5", "m3", output: 100)); // in the week, not in the 5 h window

        var r = Run();

        Assert.Equal(new TokenTotals(10, 60, 30, 1000), r.FiveHour.Total);
        Assert.Equal(100, r.FiveHour.Total.Work); // cache reads are not in Work
        Assert.Equal(new TokenTotals(10, 160, 30, 1000), r.Week.Total);
        Assert.Equal([("claude-opus-5-5", 60L), ("claude-sonnet-5", 40L)], r.FiveHour.ByModel.Select(m => (m.Model, m.Work)));
        Assert.Equal(3, r.Week.Messages);
        Assert.Equal(1, r.Files);
    }

    [Fact]
    public void Buckets_the_week_by_local_hour()
    {
        var a = Now.AddHours(-3);
        Write(@"p\s.jsonl", null, Rec(a, "m", "1", output: 5), Rec(a.AddMinutes(1), "m", "2", output: 7), Rec(Now.AddDays(-3).AddHours(-2), "m", "3", output: 100));

        var hours = Run().Week.ByHour;

        Assert.Equal(24, hours.Length);
        var b = Now.AddDays(-3).AddHours(-2);
        Assert.Equal(12, hours[a.ToLocalTime().Hour]);
        Assert.Equal(100, hours[b.ToLocalTime().Hour]);
        Assert.Equal(112, hours.Sum());
    }

    [Fact]
    public void Records_of_one_message_are_merged_and_subagents_are_counted()
    {
        // CC writes a record per content block: the partial first one and the final one share an id
        Write(@"p\s.jsonl", null, Rec(Now.AddHours(-1), "m", "same", input: 3, output: 5), Rec(Now.AddHours(-1), "m", "same", input: 3, output: 50));
        Write(@"p\s\subagents\agent-a1.jsonl", null, Rec(Now.AddHours(-1), "m", "sub", output: 9));
        Write(@"p\resumed.jsonl", null, Rec(Now.AddHours(-1), "m", "same", input: 3, output: 50)); // a resumed transcript repeating it

        var r = Run();

        Assert.Equal(new TokenTotals(3, 59, 0, 0), r.FiveHour.Total);
        Assert.Equal(2, r.FiveHour.Messages);
        Assert.Equal(3, r.Files);
    }

    [Fact]
    public void Old_files_garbled_lines_synthetic_and_missing_fields_are_skipped_or_zero()
    {
        Write(@"p\old.jsonl", Now.UtcDateTime.AddDays(-8), Rec(Now.AddHours(-1), "m", "x", output: 999)); // mtime before the window: never opened
        Write(@"p\s.jsonl", null,
            "not json {{{", "", "{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}",
            "{\"message\":{\"usage\":\"oops\"},\"timestamp\":\"" + Now.AddHours(-1).UtcDateTime.ToString("o") + "\"}", // usage of the wrong shape
            Rec(Now.AddHours(-1), "<synthetic>", "syn", output: 500),
            Rec(Now.AddHours(-1), null, null, output: 4),            // no model, no id
            Rec(Now.AddHours(-1), "m", "noUsageNumbers", usage: false) + "\"usage\"", // no usage object and a half line
            "{\"message\":{\"model\":\"m\",\"usage\":{\"output_tokens\":2}}}",        // no timestamp
            Rec(Now.AddHours(-1), "m", "ok", output: 6));

        var r = Run();

        Assert.Equal(10, r.Week.Total.Output);
        Assert.Equal([(null, 4L), ("m", 6L)], r.Week.ByModel.OrderBy(m => m.Work).Select(m => (m.Model, m.Work)));
        Assert.Equal(1, r.Files);
    }

    [Fact]
    public void No_projects_dir_is_an_empty_result()
    {
        var r = UsageStats.Compute(Path.Combine(_dir, "nope"), Now.AddHours(-5), Now.AddDays(-7));

        Assert.Equal(0, r.Files);
        Assert.Equal(default, r.Week.Total);
        Assert.Empty(r.Week.ByModel);
    }

    [Fact]
    public void Window_starts_follow_a_future_reset_else_roll_back_from_now()
    {
        var (five, week) = UsageStats.Starts(Now.AddHours(2), Now.AddDays(3), Now);
        Assert.Equal(Now.AddHours(-3), five);
        Assert.Equal(Now.AddDays(-4), week);

        (five, week) = UsageStats.Starts(Now.AddHours(-1), null, Now); // a stale reset, and none
        Assert.Equal(Now.AddHours(-5), five);
        Assert.Equal(Now.AddDays(-7), week);
    }

    [Fact]
    public void Reset_text_says_when_and_how_long_or_that_it_was_due()
    {
        Assert.Equal("", BarFormat.ResetText(null, Now, false));
        Assert.EndsWith("(in 2h 0m)", BarFormat.ResetText(Now.AddHours(2), Now, false));
        Assert.StartsWith("reset was due", BarFormat.ResetText(Now.AddMinutes(-5), Now, true));
    }
}
