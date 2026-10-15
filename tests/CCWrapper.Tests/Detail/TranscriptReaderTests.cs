using System.IO;
using CCWrapper.Data;

namespace CCWrapper.Tests.Detail;

public sealed class TranscriptReaderTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ccw-transcript-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Detail", "Fixtures", name);

    string Copy()
    {
        var path = Path.Combine(_dir, "s1.jsonl");
        File.Copy(Fixture("transcript.jsonl"), path);
        return path;
    }

    [Fact]
    public void Poll_reads_state_from_the_records_and_skips_malformed_and_unknown_lines()
    {
        var r = new TranscriptReader(Copy());

        Assert.True(r.Poll());

        Assert.Equal("claude-opus-5-5", r.Model);
        Assert.Equal("xhigh", r.Effort);
        Assert.Equal("acceptEdits", r.PermissionMode);
        Assert.Equal("Fix the bug", r.AiTitle);
        Assert.Equal(6 + 0 + 2000, r.ContextTokens);   // last turn only
        Assert.Equal(40 + 10, r.OutputTokens);         // m1 counted once although it has 4 records
        Assert.Equal(1.25, r.CostUsd);
        Assert.Equal(12, r.LinesAdded);
        Assert.Equal(3, r.LinesRemoved);
        Assert.Equal("end_turn", r.LastStopReason);
        Assert.Equal(2, r.ToolUseCount);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T10:00:06Z"), r.LastActivity);
    }

    [Fact]
    public void Messages_merge_blocks_of_one_api_message_and_leave_out_tool_results_and_meta()
    {
        var r = new TranscriptReader(Copy());
        r.Poll();

        Assert.Collection(r.Messages,
            m => { Assert.Equal("user", m.Role); Assert.Equal("Fix the bug", m.Text); Assert.Empty(m.ToolUses); },
            m =>
            {
                Assert.Equal("assistant", m.Role);
                Assert.Equal("On it.", m.Text);
                Assert.Equal([@"Edit: src\a.cs", "Bash: git status"], m.ToolUses);
            },
            m => { Assert.Equal("assistant", m.Role); Assert.Equal("Done.", m.Text); });
    }

    [Fact]
    public void Poll_returns_false_when_nothing_changed_and_picks_up_appended_records_and_a_late_completed_tail()
    {
        var path = Copy();
        var r = new TranscriptReader(path);
        r.Poll();
        Assert.False(r.Poll());

        File.AppendAllText(path, """{"type":"assistant","message":{"id":"m3","model":"<synthetic>","content":[{"type":"text","text":"API Error"}],"usage":{"input_tokens":0,"output_tokens":0}}}""" + "\n");
        File.AppendAllText(path, """{"type":"permission-mode","permissionMo""");   // half-written
        Assert.True(r.Poll());
        Assert.Equal("acceptEdits", r.PermissionMode);
        Assert.Equal("claude-opus-5-5", r.Model);                 // synthetic stubs do not replace the model
        Assert.Equal(2006, r.ContextTokens);
        Assert.Equal("API Error", r.Messages[^1].Text);
        Assert.False(r.Poll());

        File.AppendAllText(path, "de\":\"plan\"}\n");
        Assert.True(r.Poll());
        Assert.Equal("plan", r.PermissionMode);
        Assert.Equal(4, r.Messages.Count);
    }

    [Fact]
    public void Blocks_of_one_api_message_merge_even_when_another_message_is_written_in_between()
    {
        var path = Copy();
        var r = new TranscriptReader(path);
        r.Poll();
        var before = r.Messages.Count;

        File.AppendAllLines(path,
        [
            """{"type":"assistant","message":{"id":"m4","model":"claude-opus-5-5","content":[{"type":"text","text":"first"}],"usage":{"output_tokens":3}}}""",
            """{"type":"assistant","message":{"id":"m5","model":"claude-opus-5-5","content":[{"type":"text","text":"other"}],"usage":{"output_tokens":4}}}""",
            """{"type":"assistant","message":{"id":"m4","model":"claude-opus-5-5","content":[{"type":"tool_use","name":"Grep","input":{"pattern":"Foo"}}],"usage":{"output_tokens":9}}}""",
        ]);
        r.Poll();

        Assert.Equal(before + 2, r.Messages.Count);
        Assert.Equal("first", r.Messages[before].Text);
        Assert.Equal(["Grep: Foo"], r.Messages[before].ToolUses);
        Assert.Equal("other", r.Messages[before + 1].Text);
        Assert.Equal(50 + 9 + 4, r.OutputTokens);   // m4 counted once, at its final size
    }

    [Fact]
    public void Missing_file_is_not_an_error()
    {
        var r = new TranscriptReader(Path.Combine(_dir, "nope.jsonl"));

        Assert.False(r.Poll());
        Assert.Null(r.Model);
        Assert.Null(r.ContextTokens);
        Assert.Empty(r.Messages);
    }
}
