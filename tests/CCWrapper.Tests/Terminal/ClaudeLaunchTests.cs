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
    }

    [Fact]
    public void Env_copies_parent_applies_account_and_adds_tab_vars()
    {
        var tab = Guid.NewGuid();
        var parent = new Hashtable { ["Path"] = @"C:\bin", ["claude_config_dir"] = "stray", ["COLORTERM"] = "x" };

        var a = ClaudeLaunch.Env(new Account("a", null, Path.Combine(_dir, ".claude.json"), "A"), tab, parent);
        Assert.False(a.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.Equal(@"C:\bin", a["PATH"]);
        Assert.Equal(tab.ToString(), a["CCW_TAB"]);
        Assert.Equal("truecolor", a["COLORTERM"]);

        var c = ClaudeLaunch.Env(AccountC(), tab, parent);
        Assert.Equal(Path.Combine(_dir, ".claude-c"), c["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public void RelaySettings_written_when_no_statusLine_with_forward_slash_command()
    {
        var path = ClaudeLaunch.RelaySettings(AccountC(), Path.Combine(_dir, "proj"), @"C:\Apps\CCW\CCWrapper.StatusRelay.exe", _dir);

        Assert.Equal(Path.Combine(_dir, "relay-settings.json"), path);
        using var doc = JsonDocument.Parse(File.ReadAllText(path!));
        var statusLine = doc.RootElement.GetProperty("statusLine");
        Assert.Equal("command", statusLine.GetProperty("type").GetString());
        Assert.Equal("C:/Apps/CCW/CCWrapper.StatusRelay.exe", statusLine.GetProperty("command").GetString());
    }

    [Theory]
    [InlineData(".claude-c/settings.json")]
    [InlineData("proj/.claude/settings.json")]
    [InlineData("proj/.claude/settings.local.json")]
    public void RelaySettings_skipped_when_account_or_project_has_a_statusLine(string settingsFile)
    {
        var file = Path.Combine(_dir, settingsFile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{ "model": "opus", "statusLine": { "type": "command", "command": "mine.sh" } }""");

        Assert.Null(ClaudeLaunch.RelaySettings(AccountC(), Path.Combine(_dir, "proj"), @"C:\r.exe", _dir));
        Assert.False(File.Exists(Path.Combine(_dir, "relay-settings.json")));
    }

    [Fact]
    public void Relay_exe_writes_stdin_to_live_json_and_feed_applies_it()
    {
        var session = new LiveSession(AccountC());
        using var feed = new StatusLineFeed(session);
        var target = Path.Combine(StatusLineFeed.LiveDir, session.TabId + ".json");
        const string json = """{"session_id":"s9","transcript_path":"C:\\t.jsonl","workspace":{"current_dir":"C:\\w"},"model":{"display_name":"Opus"}}""";

        Assert.Equal(0, RunRelay(session.TabId.ToString(), json, out var stdout));
        Assert.Equal("", stdout);
        Assert.Equal(json, File.ReadAllText(target));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.StatusLine is null && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.Equal(("Opus", "s9", @"C:\t.jsonl", @"C:\w"), (session.StatusLine?.ModelName, session.SessionId, session.TranscriptPath, session.Cwd));

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
