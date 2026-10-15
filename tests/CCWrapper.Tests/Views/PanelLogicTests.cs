using CCWrapper.Data;
using CCWrapper.Views;

namespace CCWrapper.Tests.Views;

public sealed class PanelLogicTests
{
    static FileChange Text(string rel, int add = 1, int rem = 0, ChangeKind kind = ChangeKind.Modified) =>
        new(rel, @"C:\p\" + rel, kind, false, add, rem, 10, 10, null);

    static List<string> Lines(IEnumerable<FileNode> nodes, int depth = 0) =>
        nodes.SelectMany(n => Lines(n.Children, depth + 1).Prepend(new string(' ', depth) + n.Name + (n.IsFolder ? "/" : ""))).ToList();

    [Fact]
    public void Tree_groups_by_folder_folders_first_and_outside_files_last()
    {
        var tree = FileTree.Build([
            Text(@"src\b.cs"), Text(@"readme.md"), Text(@"src\ui\a.cs"), Text(@"src\a.cs"),
            Text(@"D:\other\x.txt"), Text(@"D:\other\y.txt"), Text(@"E:\z.txt"),
        ]);

        Assert.Equal([
            "src/", " ui/", "  a.cs", " a.cs", " b.cs",
            "readme.md",
            "outside project/", @" D:\other/", "  x.txt", "  y.txt", @" E:\/", "  z.txt",
        ], Lines(tree));
    }

    [Fact]
    public void Tree_keeps_collapsed_folders_collapsed()
    {
        var tree = FileTree.Build([Text(@"src\a.cs"), Text(@"lib\b.cs")], new HashSet<string> { @"src\" });

        Assert.Equal([false, true], tree.Select(n => n.IsExpanded).Reverse());
        Assert.Equal("src", tree[1].Name);
    }

    [Fact]
    public void Summary_by_kind_of_change()
    {
        Assert.Equal("+12 −3", FileTree.Summary(Text("a", 12, 3)));
        Assert.Equal("?", FileTree.Summary(new("a", "a", ChangeKind.Modified, false, null, null, null, 5, null)));
        Assert.Equal("+1.5 KB", FileTree.Summary(new("a", "a", ChangeKind.Added, true, null, null, 0, 1536, null)));
        Assert.Equal("−2 B", FileTree.Summary(new("a", "a", ChangeKind.Modified, true, null, null, 10, 8, null)));
        Assert.Equal("?", FileTree.Summary(new("a", "a", ChangeKind.Modified, true, null, null, null, 8, null)));
        Assert.Equal("A", new FileNode("a", "a", Text("a", kind: ChangeKind.Added)).Badge);
        Assert.Equal("", new FileNode("a", "a", Text("a")).Badge);
    }

    [Fact]
    public void Search_finds_case_insensitive_non_overlapping_matches_in_text_then_tools()
    {
        Assert.Equal([0, 2], TranscriptSearch.Offsets("aaaa", "aa"));
        Assert.Empty(TranscriptSearch.Offsets("abc", ""));

        var rows = new[]
        {
            new MessageRow(new("user", null, "Find the FOO", [])),
            new MessageRow(new("assistant", null, "no", [])),
            new MessageRow(new("assistant", null, "foo and foo", ["Bash: echo foo"])),
        };
        Assert.Equal([new Hit(0, false, 9), new Hit(2, false, 0), new Hit(2, false, 8), new Hit(2, true, 11)], TranscriptSearch.Find(rows, "foo"));
    }

    [Fact]
    public void Agent_row_formats_tokens_age_and_model()
    {
        Assert.Equal(["—", "999", "1.2k", "45.3k", "2M"], new long?[] { null, 999, 1234, 45300, 2_000_000 }.Select(AgentRow.Tokens));
        Assert.Equal(["0s", "59s", "3m", "2h", "4d"], new[] { 0, 59, 180, 7200, 4 * 86400 }.Select(s => AgentRow.FormatAge(TimeSpan.FromSeconds(s))));
        Assert.Equal("opus-4-5", AgentRow.ShortModel("claude-opus-4-5-20251101"));
        Assert.Equal("sonnet-5", AgentRow.ShortModel("claude-sonnet-5"));
        Assert.Null(AgentRow.ShortModel(null));

        var info = new SubagentInfo("id", "Explore", "Find the thing", null, null, "claude-haiku-4-5-20251001", "low", 45300, 1200, 7, null, "t.jsonl", true);
        var row = AgentRow.From(info, DateTimeOffset.Now);
        Assert.Equal("Find the thing", row.Title);
        Assert.Equal("Explore · haiku-4-5 · low", row.Detail);
        Assert.Equal("ctx 45.3k · out 1.2k · 7 tools · —", row.Stats);
    }

    [Theory]
    [InlineData("localhost:3000", "http://localhost:3000")]
    [InlineData(" 127.0.0.1:8080/x ", "http://127.0.0.1:8080/x")]
    [InlineData("example.com", "https://example.com")]
    [InlineData("https://example.com/a", "https://example.com/a")]
    [InlineData("http://localhost:5173", "http://localhost:5173")]
    [InlineData("about:blank", "about:blank")]
    public void Browser_adds_a_scheme_only_when_missing(string typed, string expected) =>
        Assert.Equal(expected, BrowserPanel.NormalizeUrl(typed));
}
