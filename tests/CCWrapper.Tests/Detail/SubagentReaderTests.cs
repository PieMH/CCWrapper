using System.IO;
using CCWrapper.Data;

namespace CCWrapper.Tests.Detail;

public sealed class SubagentReaderTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ccw-subagents-").FullName;
    readonly string _transcript;
    string AgentDir => Path.Combine(_dir, "s1", "subagents");

    public SubagentReaderTests()
    {
        _transcript = Path.Combine(_dir, "s1.jsonl");
        Directory.CreateDirectory(AgentDir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Add(string name, TimeSpan age)
    {
        foreach (var ext in new[] { ".meta.json", ".jsonl" })
            File.Copy(TranscriptReaderTests.Fixture($"agent-{name}{ext}"), Path.Combine(AgentDir, $"agent-{name}{ext}"));
        var jsonl = Path.Combine(AgentDir, $"agent-{name}.jsonl");
        File.SetLastWriteTimeUtc(jsonl, DateTime.UtcNow - age);
        return jsonl;
    }

    [Fact]
    public void Running_agent_is_listed_before_a_done_one_with_its_stats()
    {
        Add("done", TimeSpan.FromMinutes(5));
        var runPath = Add("run", TimeSpan.FromMinutes(1)); // last stop_reason is null: mid-turn
        var reader = new SubagentReader(_transcript);

        Assert.True(reader.Poll());

        var (run, done) = (reader.Agents[0], reader.Agents[1]);
        Assert.Equal("run", run.AgentId);
        Assert.True(run.IsRunning);
        Assert.Equal(("general-purpose", "Scan the repo", "toolu_run", "fork"), (run.AgentType, run.Description, run.ToolUseId, run.RequestShape));
        Assert.Equal(("claude-sonnet-5-5", "high"), (run.Model, run.Effort));
        Assert.Equal(3 + 0 + 200, run.ContextTokens);
        Assert.Equal(5 + 8, run.OutputTokens);
        Assert.Equal(2, run.ToolUseCount);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T10:00:03Z"), run.LastActivity);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T10:00:01Z"), run.FirstActivity);
        Assert.Equal(203 + 13, run.Tokens);
        Assert.Equal(runPath, run.TranscriptPath);

        Assert.Equal("done", done.AgentId);
        Assert.False(done.IsRunning);
        Assert.Equal(4 + 0 + 500, done.ContextTokens);
        Assert.Equal(10 + 120, done.OutputTokens);
        Assert.Equal(1, done.ToolUseCount);
        Assert.Equal("low", done.Effort);
    }

    [Fact]
    public void A_finished_agent_is_running_while_its_file_is_being_written_and_a_stalled_one_is_done()
    {
        var finished = Add("done", TimeSpan.FromSeconds(2));   // end_turn, but written 2 s ago
        Add("run", TimeSpan.FromHours(3));                     // never reached end_turn, nothing written for hours
        var reader = new SubagentReader(_transcript);
        reader.Poll();

        Assert.True(reader.Agents.Single(a => a.AgentId == "done").IsRunning);
        Assert.False(reader.Agents.Single(a => a.AgentId == "run").IsRunning);

        File.SetLastWriteTimeUtc(finished, DateTime.UtcNow - TimeSpan.FromMinutes(1));
        Assert.True(reader.Poll());   // flips with the clock, no new lines
        Assert.False(reader.Agents.Single(a => a.AgentId == "done").IsRunning);
        Assert.False(reader.Poll());
    }

    [Fact]
    public void Poll_picks_up_appended_lines_and_new_agents()
    {
        var runPath = Add("run", TimeSpan.FromMinutes(1));
        var reader = new SubagentReader(_transcript);
        reader.Poll();
        Assert.False(reader.Poll());

        File.AppendAllText(runPath, """{"type":"assistant","message":{"id":"r3","model":"claude-sonnet-5-5","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"cache_creation_input_tokens":0,"cache_read_input_tokens":300,"output_tokens":20}}}""" + "\n");
        File.SetLastWriteTimeUtc(runPath, DateTime.UtcNow - TimeSpan.FromMinutes(1));
        Assert.True(reader.Poll());
        Assert.False(reader.Agents[0].IsRunning);
        Assert.Equal(301, reader.Agents[0].ContextTokens);
        Assert.Equal(33, reader.Agents[0].OutputTokens);

        Add("done", TimeSpan.FromMinutes(5));
        Assert.True(reader.Poll());
        Assert.Equal(2, reader.Agents.Count);
    }

    [Fact]
    public void No_subagents_dir_or_bad_meta_is_not_an_error()
    {
        Directory.Delete(AgentDir);
        var reader = new SubagentReader(_transcript);
        Assert.False(reader.Poll());
        Assert.Empty(reader.Agents);

        Directory.CreateDirectory(AgentDir);
        File.WriteAllText(Path.Combine(AgentDir, "agent-bad.meta.json"), "{ not json");
        Assert.False(reader.Poll());
        Assert.Empty(reader.Agents);
    }
}
