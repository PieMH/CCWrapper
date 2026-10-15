using System.IO;
using CCWrapper.Core;

namespace CCWrapper.Tests;

public sealed class CoreTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ccw-core-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Discover_finds_A_and_lettered_dirs_with_global_config()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".claude"));
        File.WriteAllText(Path.Combine(_dir, ".claude.json"), """{"oauthAccount":{"emailAddress":"a@x.com"}}""");
        Directory.CreateDirectory(Path.Combine(_dir, ".claude-c"));
        File.WriteAllText(Path.Combine(_dir, ".claude-c", ".claude.json"), "{ not json");
        Directory.CreateDirectory(Path.Combine(_dir, ".claude-z")); // no .claude.json: not an account

        var accounts = AccountStore.Discover(_dir);

        Assert.Equal(["a", "c"], accounts.Select(a => a.Letter));
        var (a, c) = (accounts[0], accounts[1]);
        Assert.Null(a.ConfigDir);
        Assert.Equal(Path.Combine(_dir, ".claude"), a.ConfigRoot);
        Assert.Equal("a@x.com", a.Label);
        Assert.Equal(Path.Combine(_dir, ".claude-c"), c.ConfigRoot);
        Assert.Equal("Account C", c.Label);
        Assert.Null(AccountStore.Find("q", _dir));
        Assert.Equal("c", AccountStore.Find("C", _dir)?.Letter);
    }

    [Fact]
    public void ApplyEnv_sets_for_lettered_and_removes_any_casing_for_A()
    {
        var env = new Dictionary<string, string> { ["claude_config_dir"] = "stray", ["PATH"] = "p" };
        new Account("a", null, @"C:\h\.claude.json", "A").ApplyEnv(env);
        Assert.Equal(["PATH"], env.Keys);

        new Account("c", @"C:\h\.claude-c", @"C:\h\.claude-c\.claude.json", "C").ApplyEnv(env);
        Assert.Equal(@"C:\h\.claude-c", env["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public void Paths_encode_cwd_like_claude_code()
    {
        var paths = new ClaudePaths(@"C:\h\.claude");
        Assert.Equal(@"C:\h\.claude\projects\C--Users-me-My-Proj--x\id.jsonl", paths.Transcript(@"C:\Users\me\My Proj\.x", "id"));
    }

    [Fact]
    public void Jsonl_skips_bad_lines_and_rereads_a_half_written_tail()
    {
        var path = Path.Combine(_dir, "t.jsonl");
        File.WriteAllText(path, "{\"n\":1}\r\nnot json\n\n{\"n\":2}\n{\"n\":3,");

        var first = JsonlReader.Read(path).ToList();
        Assert.Equal([1, 2], first.Select(r => r.Json.GetProperty("n").GetInt32()));

        File.AppendAllText(path, "\"done\":true}\n{\"n\":4}");
        var rest = JsonlReader.Read(path, first[^1].Next).ToList();
        Assert.Equal([3, 4], rest.Select(r => r.Json.GetProperty("n").GetInt32()));
        Assert.Equal(new FileInfo(path).Length, rest[^1].Next);
    }

    [Fact]
    public void StatusLine_parses_documented_fields_and_tolerates_missing_ones()
    {
        var s = StatusLineData.Parse("""
            {"session_id":"s1","transcript_path":"t.jsonl","cwd":"old","workspace":{"current_dir":"C:\\p"},
             "model":{"id":"claude-opus-5-5","display_name":"Opus"},"effort":{"level":"xhigh"},
             "cost":{"total_cost_usd":1.5,"total_lines_added":10,"total_lines_removed":2},
             "context_window":{"context_window_size":1000000,"used_percentage":null,"total_input_tokens":200,"total_output_tokens":30},
             "rate_limits":{"five_hour":{"used_percentage":58.5,"resets_at":1738425600}}}
            """)!;
        Assert.Equal(("s1", "t.jsonl", @"C:\p", "claude-opus-5-5", "Opus", "xhigh"), (s.SessionId, s.TranscriptPath, s.Cwd, s.ModelId, s.ModelName, s.Effort));
        Assert.Equal((1.5, 10L, 2L), (s.CostUsd, s.LinesAdded, s.LinesRemoved));
        Assert.Equal((1000000L, (double?)null, 200L, 30L), (s.ContextWindowSize, s.ContextUsedPercent, s.TotalInputTokens, s.TotalOutputTokens));
        Assert.Equal(58.5, s.FiveHourUsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1738425600), s.FiveHourResetsAt);
        Assert.Null(s.SevenDayUsedPercent);

        Assert.Null(StatusLineData.Parse("{}")!.ModelId);
        Assert.Null(StatusLineData.Parse("garbage"));
    }
}
