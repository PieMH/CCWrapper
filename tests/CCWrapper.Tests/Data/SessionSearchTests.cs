using System.IO;
using CCWrapper.Data;

namespace CCWrapper.Tests.Data;

public sealed class SessionSearchTests : IDisposable
{
    readonly FakeHome _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void Counts_only_message_text_and_groups_per_session()
    {
        var a = _home.AddAccount("a");
        var older = DateTime.UtcNow.AddHours(-1);
        var path = _home.AddTranscript(a, @"C:\Work\Beta", "s-search", "search.jsonl", older);
        _home.AddTranscript(a, @"C:\Work\Alpha", "s-other", "titled.jsonl");
        // a subagent transcript deeper in the tree must not be searched
        FakeHome.WriteFile(Path.Combine(Path.GetDirectoryName(path)!, "s-search", "subagents", "agent-1.jsonl"),
            """{"type":"user","message":{"content":"needle in a subagent"}}""");

        var hits = SessionSearch.SearchAccount(a, "needle");

        // user string + assistant text item + user text item; not thinking, tool_use, tool_result or system
        var hit = Assert.Single(hits);
        Assert.Equal(("s-search", path, 3), (hit.SessionId, hit.Path, hit.Count));
        Assert.Equal("Please find the Needle in this haystack", hit.Snippet);
    }

    [Fact]
    public void Finds_text_that_is_escaped_in_the_raw_line()
    {
        var a = _home.AddAccount("a");
        var path = _home.AddTranscript(a, @"C:\Work\Beta", "s-search", "search.jsonl");

        var hit = SessionSearch.SearchTranscript(path, "say \"hi\" to the needle");

        Assert.Equal(1, hit!.Count);
        Assert.Equal("next: say \"hi\" to the NEEDLE", hit.Snippet);
    }

    [Fact]
    public void Snippet_is_about_120_chars_with_ellipses()
    {
        var a = _home.AddAccount("a");
        var path = a.Paths.Transcript(@"C:\Work\Beta", "long");
        FakeHome.WriteFile(path, $$"""{"type":"user","message":{"content":"{{new string('a', 200)}} needle {{new string('b', 200)}}"} }""");

        var snippet = SessionSearch.SearchTranscript(path, "needle")!.Snippet;

        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.Contains("needle", snippet);
        Assert.InRange(snippet.Length, 100, 125);
    }

    [Fact]
    public void No_match_or_blank_query_gives_nothing()
    {
        var a = _home.AddAccount("a");
        var path = _home.AddTranscript(a, @"C:\Work\Beta", "s-search", "search.jsonl");

        Assert.Empty(SessionSearch.SearchAccount(a, "absent-word"));
        Assert.Null(SessionSearch.SearchTranscript(path, "  "));
    }

    [Fact]
    public void Honours_cancellation()
    {
        var a = _home.AddAccount("a");
        _home.AddTranscript(a, @"C:\Work\Beta", "s-search", "search.jsonl");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => SessionSearch.SearchAccount(a, "needle", cts.Token));
    }
}
