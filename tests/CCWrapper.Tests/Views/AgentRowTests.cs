using CCWrapper.Data;
using CCWrapper.Views;

namespace CCWrapper.Tests.Views;

public sealed class AgentRowTests
{
    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-opus-4-5-20251101", "Opus 4.5")]
    [InlineData("claude-sonnet-5", "Sonnet 5")]
    [InlineData("claude-haiku-4-5-20251001", "Haiku 4.5")]
    [InlineData("claude-opus-4-20250514", "Opus 4")]
    [InlineData("gpt-5", "gpt-5")]
    [InlineData("claude-opus-5-5[1m]", "claude-opus-5-5[1m]")]
    public void Model_id_becomes_a_display_name(string id, string expected) => Assert.Equal(expected, AgentRow.ModelName(id));

    [Fact]
    public void Missing_model_stays_missing() => Assert.Null(AgentRow.ModelName(null));

    [Fact]
    public void Duration_is_first_to_last_activity_or_to_now_while_running()
    {
        var t0 = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        SubagentInfo Make(bool running, DateTimeOffset? first, DateTimeOffset? last) =>
            new("a", "Explore", "Find it", null, null, "claude-opus-5-5", "high", 114_000, 100, 3, last, "t.jsonl", running, first);

        Assert.Equal("2m 05s", AgentRow.From(Make(false, t0, t0.AddSeconds(125)), t0.AddHours(1)).Duration);
        Assert.Equal("1h 01m", AgentRow.From(Make(true, t0, t0.AddSeconds(125)), t0.AddMinutes(61)).Duration);
        Assert.Null(AgentRow.From(Make(false, null, t0), t0).Duration);
        Assert.Equal(["0s", "42s", "3m 05s", "2h 07m"], new[] { -5, 42, 185, 7620 }.Select(s => AgentRow.FormatDuration(TimeSpan.FromSeconds(s))));
    }

    [Fact]
    public void Subtitle_has_model_tokens_and_effort_and_skips_what_is_unknown()
    {
        var info = new SubagentInfo("a", "Explore", "Find it", null, null, "claude-opus-5-5", "high", 114_000, 100, 3, null, "t.jsonl", false);
        Assert.Equal(114_100, info.Tokens);
        Assert.Equal("Opus 5.5 · 114.1k tokens · high", AgentRow.From(info, DateTimeOffset.Now).Subtitle);

        var bare = info with { Model = null, Effort = null, ContextTokens = null };
        Assert.Null(bare.Tokens);
        Assert.Equal("", AgentRow.From(bare, DateTimeOffset.Now).Subtitle);
    }

    [Fact]
    public void Search_matches_description_and_type_ignoring_case()
    {
        var info = new SubagentInfo("a", "general-purpose", "Scan the Repo", null, null, null, null, null, 0, 0, null, "t.jsonl", false);
        Assert.True(AgentRow.Matches(info, ""));
        Assert.True(AgentRow.Matches(info, "scan the repo"));
        Assert.True(AgentRow.Matches(info, "PURPOSE"));
        Assert.False(AgentRow.Matches(info, "zzz"));
        Assert.False(AgentRow.Matches(info with { Description = null, AgentType = null }, "scan"));
    }
}
