using System.IO;
using System.Text.Json;
using CCWrapper.Core;
using CCWrapper.Views;

namespace CCWrapper.Tests.Input;

public sealed class CustomizeTests
{
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Input", "Fixtures", name);

    static JsonElement Json(string fixture) => JsonDocument.Parse(File.ReadAllText(Fixture(fixture))).RootElement;

    [Fact]
    public void Plugins_are_sorted_and_a_malformed_entry_is_still_listed()
    {
        var plugins = CustomizeData.ParsePlugins(Json("installed_plugins.json"), Json("settings.json"));

        Assert.Equal(["alpha", "broken", "loose", "zeta"], plugins.Select(p => p.Name));
        Assert.Null(plugins.Single(p => p.Name == "broken").InstallPath);
    }

    [Fact]
    public void Plugin_rows_carry_version_scope_and_path()
    {
        var plugins = CustomizeData.ParsePlugins(Json("installed_plugins.json"), Json("settings.json")).ToDictionary(p => p.Name);

        var zeta = plugins["zeta"];
        Assert.True(zeta.Enabled);
        Assert.Equal(("market", "0.2.0", "user", @"C:\cache\zeta\0.2.0"), (zeta.Marketplace, zeta.Version, zeta.Scope, zeta.InstallPath));
        Assert.Equal("market · v0.2.0 · user", zeta.Detail);

        Assert.False(plugins["alpha"].Enabled);              // listed false
        Assert.Equal("1.0.0", plugins["alpha"].Version);     // only a project install: used
        Assert.False(plugins["loose"].Enabled);              // not in enabledPlugins
        Assert.Equal("", plugins["loose"].Marketplace);
    }

    [Fact]
    public void Plugins_missing_files_give_nothing() =>
        Assert.Empty(CustomizeData.ParsePlugins(default, default(JsonElement)));

    [Fact]
    public void Project_and_local_settings_override_the_user_enabledPlugins()
    {
        var project = JsonDocument.Parse("""{ "enabledPlugins": { "loose": true, "alpha@market": true } }""").RootElement;
        var local = JsonDocument.Parse("""{ "enabledPlugins": { "alpha@market": false } }""").RootElement;
        var enabled = CustomizeData.ParsePlugins(Json("installed_plugins.json"), Json("settings.json"), project, local, default)
            .ToDictionary(p => p.Name, p => p.Enabled);

        Assert.True(enabled["zeta"]);   // user settings only
        Assert.True(enabled["loose"]);  // project settings
        Assert.False(enabled["alpha"]); // user off, project on, local off
    }

    [Fact]
    public void Mcp_servers_show_url_without_query_or_command_with_args()
    {
        var rows = CustomizeData.ParseMcp(Json("claude.json").GetProperty("mcpServers"), "user");

        Assert.Equal([new McpRow("tool", "user", @"C:\Tools\tool.exe mcp --flag"), new McpRow("remote", "user", "https://example.test/mcp")], rows);
        Assert.Empty(CustomizeData.ParseMcp(default, "user"));
    }

    [Theory]
    [InlineData(@"C:\Work\Proj", "local")]
    [InlineData(@"c:\work\proj\", "local")]
    [InlineData(@"C:\Work\Other", "other")]
    public void Project_servers_match_the_cwd_across_slashes_and_case(string cwd, string server)
    {
        var rows = CustomizeData.ParseMcp(CustomizeData.ProjectServers(Json("claude.json"), cwd), "project");
        Assert.Equal(server, Assert.Single(rows).Name);
    }

    [Fact]
    public void Project_servers_of_an_unknown_cwd_are_none() =>
        Assert.Empty(CustomizeData.ParseMcp(CustomizeData.ProjectServers(Json("claude.json"), @"C:\Elsewhere"), "project"));

    [Fact]
    public void Mcp_json_file_is_read_like_the_global_config() =>
        Assert.Equal(new McpRow("fromfile", ".mcp.json", "npx -y pkg"), Assert.Single(CustomizeData.ParseMcp(Json("mcp.json").GetProperty("mcpServers"), ".mcp.json")));

    [Theory]
    [InlineData("---\nname: a\ndescription: Short one\n---\nbody", "a", "Short one")]
    [InlineData("---\nname: a\ndescription: >-\n  Folded one.\n  Second line.\nother: x\n---\n", "a", "Folded one. Second line.")]
    [InlineData("---\r\nname: \"q\"\r\ndescription: 'quoted'\r\n---\r\n", "q", "quoted")]
    [InlineData("---\nname: a\ndescription: plain\n  continued\n---", "a", "plain continued")]
    [InlineData("---\ndescription: only\n---", null, "only")]
    [InlineData("# no frontmatter\nname: x", null, null)]
    [InlineData("", null, null)]
    public void Frontmatter_reads_name_and_description(string text, string? name, string? description) =>
        Assert.Equal((name, description), CustomizeData.Frontmatter(text));

    [Fact]
    public void Skills_are_read_from_folders_with_a_folder_name_fallback()
    {
        var skills = CustomizeData.ReadSkills(Fixture("skills"), "user").OrderBy(s => s.Name).ToList();

        Assert.Equal([
            new SkillRow("alpha-skill", "Does alpha things. Use when alpha is needed.", "user"),
            new SkillRow("beta-skill", "Quoted beta.", "user"),
            new SkillRow("gamma", "", "user"),
        ], skills);
        Assert.Empty(CustomizeData.ReadSkills(Fixture("no-such-folder"), "user"));
    }

    static readonly Account Acct = new("c", @"C:\nowhere", @"C:\nowhere\.claude.json", "t");
    static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task McpList_returns_the_output_of_the_process()
    {
        var text = await CustomizeData.McpListAsync(Acct, null, TimeSpan.FromSeconds(20), Cmd, "/c echo connector-ok");
        Assert.Equal("connector-ok", text);
    }

    [Fact]
    public async Task McpList_gives_up_after_the_timeout()
    {
        var text = await CustomizeData.McpListAsync(Acct, null, TimeSpan.FromMilliseconds(300), Cmd, "/c ping -n 30 127.0.0.1");
        Assert.StartsWith("Timed out", text);
    }

    [Fact]
    public async Task McpList_gives_up_when_a_child_keeps_the_output_open_after_exit()
    {
        // start /b leaves ping holding the inherited stdout pipe after cmd exits
        var text = await CustomizeData.McpListAsync(Acct, null, TimeSpan.FromSeconds(1), Cmd, "/c start /b ping -n 15 127.0.0.1 >nul & echo done");
        Assert.StartsWith("Timed out", text);
    }

    [Fact]
    public async Task McpList_passes_the_account_env()
    {
        var text = await CustomizeData.McpListAsync(Acct, null, TimeSpan.FromSeconds(20), Cmd, "/c echo %CLAUDE_CONFIG_DIR%");
        Assert.Equal(@"C:\nowhere", text);
    }
}
