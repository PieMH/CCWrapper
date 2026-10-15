using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CCWrapper.Core;
using CCWrapper.Data;

namespace CCWrapper.Tests.Data;

public sealed class SessionCatalogTests : IDisposable
{
    const string Alpha = @"C:\Work\Alpha";
    readonly FakeHome _home = new();
    static readonly int Self = Environment.ProcessId;

    public void Dispose() => _home.Dispose();

    [Fact]
    public void Titles_follow_the_priority_chain_and_list_is_newest_first()
    {
        var a = _home.AddAccount("a");
        var t = DateTime.UtcNow;
        _home.AddTranscript(a, Alpha, "custom1", "titled.jsonl", t.AddMinutes(-1));
        FakeHome.WriteFile(Path.Combine(a.Paths.ProjectDir(Alpha), "custom1", "custom-title.json"), """{"customTitle":"Renamed by me"}""");
        _home.AddTranscript(a, Alpha, "ai1", "titled.jsonl", t.AddMinutes(-2));
        _home.AddTranscript(a, @"C:\Work\Beta", "prompt1", "prompt-only.jsonl", t.AddMinutes(-3));
        _home.AddTranscript(a, @"C:\Work\Gamma", "hist1", "bare.jsonl", t.AddMinutes(-4));
        _home.AddTranscript(a, @"C:\Work\Gamma", "bare1", "bare.jsonl", t.AddMinutes(-5));
        FakeHome.WriteFile(a.Paths.History,
            """
            {"display":"unrelated","sessionId":"other","project":"x","timestamp":1}
            {"display":"first prompt from history","sessionId":"hist1","project":"x","timestamp":2}
            {"display":"later prompt","sessionId":"hist1","project":"x","timestamp":3}
            """);
        // subagent transcripts are not sessions
        FakeHome.WriteFile(Path.Combine(a.Paths.ProjectDir(Alpha), "ai1", "subagents", "agent-1.jsonl"), """{"type":"user","cwd":"C:\\x"}""");

        var list = SessionCatalog.List(a);

        Assert.Equal(["custom1", "ai1", "prompt1", "hist1", "bare1"], list.Select(s => s.Id));
        Assert.Equal(["Renamed by me", "New AI title", "first line second line", "first prompt from history", "bare1"], list.Select(s => s.Title));
        Assert.Equal([Alpha, Alpha, @"C:\Work\Beta", @"C:\Work\Gamma", @"C:\Work\Gamma"], list.Select(s => s.Cwd));
        Assert.Equal(new DateTimeOffset(t.AddMinutes(-1)), list[0].LastActivity);
        Assert.All(list, s => Assert.Equal(a, s.Account));
        Assert.EndsWith(@"custom1.jsonl", list[0].Path);
    }

    [Fact]
    public void Only_the_tail_is_read_for_titles()
    {
        var a = _home.AddAccount("a");
        var pad = string.Concat(Enumerable.Repeat("""{"type":"assistant","message":{"content":"padding padding padding padding padding"}}""" + "\n", 3000));
        var path = a.Paths.Transcript(Alpha, "big");
        FakeHome.WriteFile(path,
            "{\"type\":\"user\",\"cwd\":\"C:\\\\Work\\\\Alpha\"}\n{\"type\":\"ai-title\",\"aiTitle\":\"buried in the head\"}\n" + pad +
            "{\"type\":\"last-prompt\",\"lastPrompt\":\"in the tail\"}\n");
        Assert.True(new FileInfo(path).Length > 128 * 1024);

        var entry = Assert.Single(SessionCatalog.List(a));

        Assert.Equal("in the tail", entry.Title);
        Assert.Equal(Alpha, entry.Cwd);
    }

    [Fact]
    public void Probe_is_cached_by_size_and_mtime()
    {
        var a = _home.AddAccount("a");
        var mtime = DateTime.UtcNow.AddMinutes(-10);
        var path = _home.AddTranscript(a, Alpha, "c1", "prompt-only.jsonl", mtime);
        Assert.Equal("first line second line", SessionCatalog.List(a)[0].Title);

        // same size, same mtime: the cache answers, the edit is not seen
        File.WriteAllText(path, File.ReadAllText(path).Replace("second   line", "THIRD    LINE"));
        File.SetLastWriteTimeUtc(path, mtime);
        Assert.Equal("first line second line", SessionCatalog.List(a)[0].Title);

        File.SetLastWriteTimeUtc(path, mtime.AddSeconds(5));
        Assert.Equal("first line THIRD LINE", SessionCatalog.List(a)[0].Title);
    }

    [Fact]
    public void Running_registry_keeps_live_pids_and_merges_into_the_list()
    {
        var a = _home.AddAccount("a");
        var x = _home.AddAccount("x");
        _home.AddTranscript(a, Alpha, "live1", "bare.jsonl");
        _home.AddTranscript(a, Alpha, "ghost1", "bare.jsonl");
        _home.AddTranscript(x, Alpha, "reused1", "bare.jsonl");
        FakeHome.AddRegistry(a, Self, "live1", status: "waiting", extra: ",\"waitingFor\":\"permission prompt\"");
        FakeHome.AddRegistry(a, FakeHome.DeadPid(), "ghost1", procStart: "1");
        FakeHome.AddRegistry(x, Self, "reused1", procStart: "100000000000000000"); // alive pid, but another creation time: Windows reused it
        FakeHome.WriteFile(Path.Combine(a.Paths.Sessions, "junk.json"), "{ not json");

        var running = Assert.Single(SessionCatalog.Running(a));
        var list = SessionCatalog.List(a);

        Assert.Equal((Self, "live1", Alpha, "n", SessionStatus.Waiting, "permission prompt", "interactive"),
            (running.Pid, running.SessionId, running.Cwd, running.Name, running.Status, running.WaitingFor, running.Kind));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792046000000), running.UpdatedAt);
        Assert.Equal(running, list.Single(s => s.Id == "live1").Running);
        Assert.Null(list.Single(s => s.Id == "ghost1").Running);
        Assert.Empty(SessionCatalog.Running(x));
        Assert.Null(SessionCatalog.List(x).Single().Running);
    }

    [Fact]
    public void Recent_projects_merge_global_config_and_transcripts_and_drop_missing_folders()
    {
        var onlyInConfig = Directory.CreateDirectory(Path.Combine(_home.Home, "cfg-only")).FullName;
        var inBoth = Directory.CreateDirectory(Path.Combine(_home.Home, "both")).FullName;
        var gone = Path.Combine(_home.Home, "deleted");
        // Claude Code writes both slash styles as keys
        string Key(string p, bool slashes) => JsonSerializer.Serialize(slashes ? p.Replace(Path.DirectorySeparatorChar, '/') : p);
        var a = _home.AddAccount("a",
            "{\"projects\":{" + Key(onlyInConfig, true) + ":{\"lastStartTime\":1000000000000}," + Key(inBoth, true) + ":{}," + Key(gone, false) + ":{}}}");
        FakeHome.WriteFile(a.Paths.Transcript(inBoth, "s1"), "{\"type\":\"user\",\"cwd\":" + JsonSerializer.Serialize(inBoth) + "}");

        var projects = SessionCatalog.RecentProjects(a);

        Assert.Equal([inBoth, onlyInConfig], projects.Select(p => p.Path)); // newest use first; "deleted" is gone; no duplicate for inBoth
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1000000000000), projects[1].LastUsed);
    }

    [Fact]
    public void Recent_lists_sessions_of_all_accounts_newest_first()
    {
        var a = _home.AddAccount("a");
        var x = _home.AddAccount("x");
        var t = DateTime.UtcNow;
        _home.AddTranscript(a, Alpha, "a-old", "bare.jsonl", t.AddHours(-3));
        _home.AddTranscript(x, Alpha, "x-new", "bare.jsonl", t.AddHours(-1));
        _home.AddTranscript(a, Alpha, "a-mid", "bare.jsonl", t.AddHours(-2));

        var recent = SessionCatalog.Recent(AccountStore.Discover(_home.Home), take: 2);

        Assert.Equal([("x", "x-new"), ("a", "a-mid")], recent.Select(s => (s.Account.Letter, s.Id)));
    }
}
