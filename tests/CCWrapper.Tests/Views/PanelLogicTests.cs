using System.Reflection;
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
        var text = new FileNode("a", "a", Text("a", 12, 3));
        Assert.Equal(("+12", "−3", ""), (text.Plus, text.Minus, text.Other)); // colored apart
        var binary = new FileNode("a", "a", new("a", "a", ChangeKind.Added, true, null, null, 0, 1536, null));
        Assert.Equal(("", "", "+1.5 KB"), (binary.Plus, binary.Minus, binary.Other));
        Assert.Equal(("", "", ""), (new FileNode("dir", "dir").Plus, new FileNode("dir", "dir").Minus, new FileNode("dir", "dir").Other));
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
    [InlineData("data:text/html,<h1>x</h1>", "data:text/html,<h1>x</h1>")]
    public void Browser_adds_a_scheme_only_when_missing(string typed, string expected) =>
        Assert.Equal(expected, BrowserPanel.NormalizeUrl(typed));

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("HTTP://localhost:3000", true)]
    [InlineData("about:blank", true)]
    [InlineData("ABOUT:Blank#x", true)]
    [InlineData("about:srcdoc", false)]
    [InlineData("about:blank?x", false)]
    [InlineData("about:", false)]
    [InlineData("about:version", false)]
    [InlineData("data:text/html,<h1>x</h1>", true)]
    [InlineData("blob:https://example.com/1", true)]
    [InlineData("file:///C:/Users/x/.claude/.credentials.json", false)]
    [InlineData("FILE:///C:/x", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("view-source:https://example.com", false)]
    [InlineData("edge://settings", false)]
    [InlineData("chrome-extension://abc/x.html", false)]
    [InlineData("", false)]
    [InlineData("example.com", false)] // not normalized yet: no scheme
    public void Browser_lets_pages_and_Claude_open_only_web_schemes(string url, bool allowed) =>
        Assert.Equal(allowed, BrowserPanel.SchemeAllowed(url));

    [Fact]
    public void Browser_tools_refuse_a_page_that_is_not_a_web_address()
    {
        Assert.Null(BrowserPanel.NotAWebPage("https://example.com/"));
        Assert.Equal("The Browser panel shows a page that is not a web address (file:); Claude's tools work only on web pages. Navigate to a web address first.",
            BrowserPanel.NotAWebPage("file:///C:/x.txt"));
        Assert.Contains("(edge:)", BrowserPanel.NotAWebPage("edge://settings"));
        Assert.Contains("(about:)", BrowserPanel.NotAWebPage("about:version"));
        Assert.NotNull(BrowserPanel.NotAWebPage(""));
        Assert.NotNull(BrowserPanel.NotAWebPage(null));
    }

    [Fact]
    public void Browser_new_tab_and_navigate_refuse_a_non_web_scheme()
    {
        Assert.Null(BrowserPanel.OpenRefusal("new_tab", BrowserPanel.NormalizeUrl("example.com")));
        Assert.Null(BrowserPanel.OpenRefusal("new_tab", "about:blank"));
        Assert.Equal("Refused file:///C:/x.txt: the new_tab tool opens only http, https, blob, about:blank and data addresses.",
            BrowserPanel.OpenRefusal("new_tab", BrowserPanel.NormalizeUrl("file:///C:/x.txt")));
        Assert.StartsWith("Refused edge://settings: the navigate tool", BrowserPanel.OpenRefusal("navigate", "edge://settings"));
    }

    [Fact]
    public void Browser_tab_list_hides_the_title_and_url_of_a_page_that_is_not_a_web_address() =>
        Assert.Equal("Tab 1: Example - https://example.com/\nTab 3 (front): (not a web page)\nTab 4: (not a web page)\nTab 5: Two - data:text/html,x",
            BrowserPanel.DescribeTabs([(1, "Example", "https://example.com/", false), (3, "secret.txt", "file:///C:/secret.txt", true),
                (4, null, null, false), (5, "Two", "data:text/html,x", false)]));

    [Theory]
    [InlineData("https://example.com/", "Example", "Example - https://example.com/")]
    [InlineData("data:text/html,<h1>t8</h1>", "data:text/html,<h1>t8</h1>", "data:text/html,<h1>t8</h1>")] // no <title>: WebView2 reports the URL
    [InlineData("https://example.com/", "", "https://example.com/")]
    public void Browser_page_text_drops_a_title_that_only_repeats_the_url(string url, string title, string expected) =>
        Assert.Equal(expected, BrowserPanel.Page(url, title));

    [Fact]
    public void Browser_page_text_cuts_a_long_url_at_80_characters()
    {
        var url = "data:text/html," + new string('x', 200);
        var shown = BrowserPanel.Page(url, url);
        Assert.Equal(80, shown.Length);
        Assert.EndsWith("…", shown);
    }

    [Fact]
    public void Browser_tab_ids_count_up_never_reused_and_the_new_tab_is_in_front()
    {
        var tabs = new BrowserTabs<string>();
        Assert.Equal(0, tabs.Front);
        Assert.Null(tabs.FrontTab);
        Assert.Equal([1, 2, 3], new[] { tabs.Add("a"), tabs.Add("b"), tabs.Add("c") });
        Assert.Equal(("c", 3), (tabs.FrontTab, tabs.Front));
        Assert.True(tabs.Switch(1));
        Assert.False(tabs.Switch(9));
        Assert.Equal(1, tabs.Front);
        Assert.True(tabs.Remove(3));
        Assert.False(tabs.Remove(3));
        Assert.Equal(4, tabs.Add("d"));
        Assert.Equal(["a", "b", "d"], tabs.All.Select(t => t.Tab));
        Assert.Null(tabs[3]);
    }

    [Fact]
    public void Browser_closing_the_front_tab_brings_its_right_neighbor_else_its_left_and_the_last_leaves_none()
    {
        var tabs = new BrowserTabs<string>();
        tabs.Add("a"); tabs.Add("b"); tabs.Add("c");
        tabs.Switch(2);
        tabs.Remove(2);
        Assert.Equal(3, tabs.Front);
        tabs.Remove(3);
        Assert.Equal(1, tabs.Front);
        tabs.Add("d");
        tabs.Switch(1);
        tabs.Remove(4); // not the front one: the front stays
        Assert.Equal(1, tabs.Front);
        tabs.Remove(1);
        Assert.Equal((0, 0), (tabs.Front, tabs.All.Count)); // the panel then opens a blank tab: id 5, not 1
        Assert.Equal(5, tabs.Add("e"));
    }

    [Fact]
    public void Browser_opens_at_most_twenty_tabs()
    {
        var tabs = new BrowserTabs<string>();
        for (var i = 1; i <= 20; i++) Assert.Equal(i, tabs.Add("t"));
        Assert.True(tabs.Full);
        Assert.Equal(0, tabs.Add("over"));
        Assert.Equal((20, 20), (tabs.All.Count, tabs.Front));
        tabs.Remove(5);
        Assert.Equal(21, tabs.Add("again"));
    }

    [Theory]
    [InlineData("12", "[data-ccw-ref=\"12\"]")]
    [InlineData("#login", "#login")]
    [InlineData("a1", "a1")]
    [InlineData("", "")]
    [InlineData("١٢", "١٢")] // non-ASCII digits are a selector, not a ref
    public void Browser_target_is_a_ref_only_when_all_ascii_digits(string target, string selector) =>
        Assert.Equal(selector, BrowserScripts.Selector(target));

    [Fact]
    public void Browser_script_arguments_enter_as_json_literals()
    {
        var evil = "x\"); alert(1); (\"</script>\n";
        var js = BrowserScripts.Call("(s, t) => s", evil, "t");
        Assert.Equal("((s, t) => s)(\"x\\u0022); alert(1); (\\u0022\\u003C/script\\u003E\\n\",\"t\")", js);
    }

    [Fact]
    public void WebView2_update_renamed_the_window_close_handler_fix_DropControlsCloseHandler()
    {
        var field = typeof(Microsoft.Web.WebView2.Core.CoreWebView2).GetField(BrowserPanel.CloseEventField, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Equal(typeof(EventHandler<object>), field?.FieldType);
        // not a base of WebView2 in this package: the control's helper class that holds the handler
        var control = typeof(Microsoft.Web.WebView2.Wpf.WebView2).Assembly.GetType("Microsoft.Web.WebView2.Wpf.WebView2Base");
        Assert.NotNull(control?.GetMethod(BrowserPanel.ControlCloseHandler, BindingFlags.NonPublic | BindingFlags.Instance));
    }
}
