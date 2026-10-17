using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;

namespace CCWrapper.Tests.Terminal;

public sealed class ClaudeLaunchTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ccw-launch-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    Account AccountC() => new("c", Path.Combine(_dir, ".claude-c"), Path.Combine(_dir, ".claude-c", ".claude.json"), "C");

    [Fact]
    public void Args_new_session_passes_only_set_flags()
    {
        Assert.Equal(["--session-id", "s1"], ClaudeLaunch.Args(new LaunchOptions(@"C:\p"), "s1", null));
        Assert.Equal(["--session-id", "s1", "--model", "opus", "--effort", "high", "--permission-mode", "plan", "-n", "my task", "--settings", @"C:\r.json"],
            ClaudeLaunch.Args(new LaunchOptions(@"C:\p", null, "opus", "high", "plan", "my task"), "s1", @"C:\r.json"));
    }

    [Fact]
    public void Args_resume_uses_the_resume_id()
    {
        Assert.Equal(["--resume", "old", "--effort", "low"],
            ClaudeLaunch.Args(new LaunchOptions(@"C:\p", ResumeId: "old", Effort: "low", Name: " "), "ignored", null));
        Assert.Equal(["--resume", "old", "--fork-session"], ClaudeLaunch.Args(new LaunchOptions(@"C:\p", ResumeId: "old", Fork: true), "ignored", null));
        Assert.Equal(["--session-id", "s1"], ClaudeLaunch.Args(new LaunchOptions(@"C:\p", Fork: true), "s1", null)); // nothing to copy
    }

    [Fact]
    public void Env_copies_parent_applies_account_and_adds_tab_vars()
    {
        var tab = Guid.NewGuid();
        var parent = new Hashtable
        {
            ["Path"] = @"C:\bin", ["claude_config_dir"] = "stray", ["COLORTERM"] = "x",
            ["CLAUDECODE"] = "1", ["ai_agent"] = "claude-code", ["CLAUDE_CODE_EXECPATH"] = @"C:\parent\claude.exe",
        };

        var a = ClaudeLaunch.Env(new Account("a", null, Path.Combine(_dir, ".claude.json"), "A"), tab, parent);
        Assert.False(a.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.All(new[] { "CLAUDECODE", "AI_AGENT", "CLAUDE_CODE_EXECPATH" }, name => Assert.False(a.ContainsKey(name)));
        Assert.Equal(@"C:\bin", a["PATH"]);
        Assert.Equal(tab.ToString(), a["CCW_TAB"]);
        Assert.Equal("truecolor", a["COLORTERM"]);

        var c = ClaudeLaunch.Env(AccountC(), tab, parent);
        Assert.Equal(Path.Combine(_dir, ".claude-c"), c["CLAUDE_CONFIG_DIR"]);
    }

    string Relay()
    {
        var exe = Path.Combine(_dir, "CCW", "CCWrapper.StatusRelay.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        return exe;
    }

    [Fact]
    public void RelaySettings_is_inline_json_with_a_forward_slash_command_and_writes_no_file()
    {
        var exe = Relay();
        var json = ClaudeLaunch.RelaySettings(AccountC(), Path.Combine(_dir, "proj"), exe);

        using var doc = JsonDocument.Parse(json!);
        var statusLine = doc.RootElement.GetProperty("statusLine");
        Assert.Equal("command", statusLine.GetProperty("type").GetString());
        Assert.Equal(exe.Replace('\\', '/'), statusLine.GetProperty("command").GetString());
        Assert.Equal([exe], Directory.GetFiles(_dir, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(@"C:\Users\jane\AppData\Local\PieMH.CCWrapper\current\a-b_c~1.exe", true)]
    [InlineData(@"C:\Users\John Smith\r.exe", false)]
    [InlineData(@"C:\Users\O'Brien\r.exe", false)]
    [InlineData(@"C:\Users\A&B\r.exe", false)]
    [InlineData(@"C:\Users\a(1)\r.exe", false)]
    [InlineData(@"C:\Users\a;b$c`d\r.exe", false)]
    [InlineData("C:\\Users\\Jos\u00e9\\r.exe", false)]
    public void IsShellSafe_allows_only_plain_path_characters(string path, bool expected) =>
        Assert.Equal(expected, ClaudeLaunch.IsShellSafe(path));

    [Fact]
    public void RelaySettings_with_a_space_in_the_path_uses_the_short_path_or_skips_the_relay()
    {
        var exe = Path.Combine(_dir, "John Smith", "CCWrapper.StatusRelay.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "same file");

        var json = ClaudeLaunch.RelaySettings(AccountC(), Path.Combine(_dir, "proj"), exe);

        if (json is null) return; // 8.3 names are off on this volume
        using var doc = JsonDocument.Parse(json);
        var command = doc.RootElement.GetProperty("statusLine").GetProperty("command").GetString()!;
        Assert.DoesNotContain(' ', command);
        Assert.Equal("same file", File.ReadAllText(command));
    }

    [Fact]
    public void RelaySettings_skipped_when_the_relay_exe_is_missing() =>
        Assert.Null(ClaudeLaunch.RelaySettings(AccountC(), Path.Combine(_dir, "proj"), Path.Combine(_dir, "gone.exe")));

    [Theory]
    [InlineData(".claude-c/settings.json")]
    [InlineData("proj/.claude/settings.json")]
    [InlineData("proj/.claude/settings.local.json")]
    public void RelaySettings_skipped_when_account_or_project_has_a_statusLine(string settingsFile)
    {
        var file = Path.Combine(_dir, settingsFile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{ "model": "opus", "statusLine": { "type": "command", "command": "mine.sh" } }""");

        Assert.Null(ClaudeLaunch.RelaySettings(AccountC(), Path.Combine(_dir, "proj"), Relay()));
    }

    [Fact]
    public void Relay_exe_writes_stdin_to_live_json_and_feed_applies_it()
    {
        var session = new LiveSession(AccountC()) { Cwd = @"C:\launch" };
        using var feed = new StatusLineFeed(session);
        var target = Path.Combine(StatusLineFeed.LiveDir, session.TabId + ".json");
        const string json = """{"session_id":"s9","transcript_path":"C:\\t.jsonl","workspace":{"current_dir":"C:\\w"},"model":{"display_name":"Opus"}}""";

        Assert.Equal(0, RunRelay(session.TabId.ToString(), json, out var stdout));
        Assert.Equal("", stdout);
        Assert.Equal(json, File.ReadAllText(target));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.TranscriptPath is null && DateTime.UtcNow < deadline) Thread.Sleep(50); // the last value Apply sets
        // Cwd stays the launcher's: the status line's current_dir follows Claude's own cd.
        Assert.Equal(("Opus", "s9", @"C:\t.jsonl", @"C:\w", @"C:\launch"),
            (session.StatusLine?.ModelName, session.SessionId, session.TranscriptPath, session.StatusLine?.Cwd, session.Cwd));

        feed.Dispose();
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Relay_exe_ignores_a_tab_that_is_not_a_guid()
    {
        Assert.Equal(0, RunRelay(@"..\..\evil", "{}", out var stdout));
        Assert.Equal("", stdout);
        Assert.False(File.Exists(Path.Combine(StatusLineFeed.LiveDir, @"..\..\evil.json")));
    }

    [Fact]
    public async Task UsageProbe_skips_without_starting_claude_while_the_saved_usage_is_fresh()
    {
        var account = AccountC();
        Directory.CreateDirectory(account.ConfigDir!);
        File.WriteAllText(account.GlobalConfigPath, $$"""{ "cachedUsageUtilization": { "fetchedAtMs": {{DateTimeOffset.Now.AddSeconds(-30).ToUnixTimeMilliseconds()}} } }""");
        var sw = Stopwatch.StartNew();
        Assert.False(await UsageProbe.RefreshAsync(account));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5)); // a real /usage run takes 2 s or more
    }

    static int RunRelay(string tab, string stdin, out string stdout)
    {
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "CCWrapper.StatusRelay.exe"))
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false,
        };
        psi.Environment["CCW_TAB"] = tab;
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
