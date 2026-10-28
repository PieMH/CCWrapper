using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;

namespace CCWrapper.Tests.Input;

/// <summary>Plugins &amp; connectors: what claude plugin/mcp print, parsed; the commands built; the CLI runner.</summary>
public sealed class ExtensionsTests
{
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Input", "Fixtures", name);

    static JsonElement Json(string fixture) => JsonDocument.Parse(File.ReadAllText(Fixture(fixture))).RootElement;

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---- plugins ----

    const string PluginList = """
        [
          { "id": "alpha@market", "version": "2.0.0", "scope": "project", "enabled": true, "projectPath": "C:\\Work",
            "installedAt": "2026-10-01T10:00:00Z", "lastUpdated": "2026-10-02T10:00:00Z", "notes": ["a note"] },
          { "id": "alpha@market", "version": "2.1.0", "scope": "user", "enabled": false, "installPath": "C:\\cache\\alpha",
            "installedAt": "2026-10-03T10:00:00Z", "lastUpdated": "2026-10-09T10:00:00Z", "notes": ["a note"],
            "mcpServers": { "alpha-server": { "command": "npx" } } },
          { "id": "zeta@synced", "version": "1.2.0", "scope": "synced", "enabled": true },
          { "id": "Beta@other", "scope": "local", "enabled": true },
          { "version": "no id: skipped" }
        ]
        """;

    [Fact]
    public void Installed_plugins_merge_their_installs_and_the_user_install_leads()
    {
        var plugins = PluginData.ParseInstalled(Parse(PluginList));

        Assert.Equal(["alpha@market", "Beta@other", "zeta@synced"], plugins.Select(p => p.Id));
        var alpha = plugins[0];
        Assert.Equal(("alpha", "market", "user", "2.1.0", false), (alpha.Name, alpha.Marketplace, alpha.Main.Scope, alpha.Main.Version, alpha.Enabled));
        Assert.Equal("project + user", alpha.Scopes);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T10:00:00Z"), alpha.InstalledAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-09T10:00:00Z"), alpha.Updated);
        Assert.Equal(["a note"], alpha.Notes);
        Assert.Equal(["alpha-server"], alpha.McpServers);
        Assert.False(alpha.Synced);
        Assert.True(plugins[2].Synced);
        Assert.Equal(("local", null), (plugins[1].Main.Scope, plugins[1].Main.Version));
    }

    [Fact]
    public void Available_json_gives_the_installed_plugins_and_the_catalog()
    {
        var root = Parse("""
            { "installed": [ { "id": "alpha@market", "scope": "user", "enabled": true } ],
              "available": [
                { "pluginId": "big@official", "name": "big", "description": "Does big things", "marketplaceName": "official", "installCount": 5000 },
                { "pluginId": "bare@dir" },
                { "name": "no id: skipped" } ] }
            """);

        Assert.Equal("alpha@market", Assert.Single(PluginData.ParseInstalled(root)).Id);
        Assert.Equal([new CatalogEntry("big@official", "big", "Does big things", "official", 5000), new CatalogEntry("bare@dir", "bare@dir", "", "", 0)],
            PluginData.ParseCatalog(root));
    }

    [Fact]
    public void Catalog_search_matches_name_or_description_most_installed_first()
    {
        CatalogEntry[] catalog =
        [
            new("a@m", "browser-tools", "Drive a browser", "m", 10),
            new("b@m", "pdf", "Read PDFs in the browser", "m", 300),
            new("c@x", "browse", "Lists files", "x", 50),
            new("d@m", "unrelated", "Nothing", "m", 999),
        ];

        Assert.Equal(("b@m c@x a@m", 3), Ids(PluginData.Search(catalog, " BROWS ", null, 10)));
        Assert.Equal(("b@m", 3), Ids(PluginData.Search(catalog, "brows", null, 1)));
        Assert.Equal(("b@m a@m", 2), Ids(PluginData.Search(catalog, "brows", "m", 10)));
        Assert.Equal(4, PluginData.Search(catalog, "", null, 10).Matches);

        static (string, int) Ids((List<CatalogEntry> Shown, int Matches) r) => (string.Join(' ', r.Shown.Select(c => c.Id)), r.Matches);
    }

    [Fact]
    public void Show_more_raises_the_cap_with_the_same_query_filter_and_order()
    {
        var catalog = Enumerable.Range(1, 150).Select(i => new CatalogEntry($"p{i}@m", $"plug{i}", "", i % 2 == 0 ? "m" : "x", i)).ToList();

        var (first, matches) = PluginData.Search(catalog, "plug", "m", 60);
        var (second, again) = PluginData.Search(catalog, "plug", "m", 120);

        Assert.Equal((60, 75, 75), (first.Count, matches, again));
        Assert.Equal(75, second.Count); // everything shown: the button is hidden when matches == shown
        Assert.Equal(first.Select(c => c.Id), second.Take(60).Select(c => c.Id)); // same order, the top results stay first
        Assert.Equal("p150@m", second[0].Id);
    }

    const string MarketplaceJson = """
        { "name": "mk", "plugins": [
          { "name": "other", "source": "./plugins/other" },
          { "name": "alpha", "version": "1.2.0", "author": { "name": "Ann", "email": "a@x.test" }, "homepage": "https://alpha.test",
            "repository": { "url": "https://github.com/ann/alpha" }, "license": "MIT", "keywords": ["k1", "k2"], "tags": ["k2", "k3"],
            "category": "tools", "source": "./plugins/alpha" } ] }
        """;

    [Fact]
    public void The_marketplace_manifest_gives_every_field_and_the_missing_ones_stay_empty()
    {
        var m = PluginData.ParseManifest(Parse(MarketplaceJson), "alpha", "https://github.com/owner/market");

        Assert.Equal(("1.2.0", "Ann", "https://alpha.test", "https://github.com/ann/alpha", "MIT", "tools"),
            (m.Version, m.Author, m.Homepage, m.Repository, m.License, m.Category));
        Assert.Equal(["k1", "k2", "k3"], m.Keywords);
        Assert.Equal("https://github.com/owner/market/tree/HEAD/plugins/alpha", m.SourceUrl);

        var none = PluginData.ParseManifest(Parse(MarketplaceJson), "missing", "https://github.com/owner/market");
        Assert.Equal((null, null, null, null, null, null, 0), (none.Version, none.Author, none.Homepage, none.Repository, none.License, none.Category, none.Keywords.Count));
        Assert.Null(none.SourceUrl);
        Assert.Null(PluginData.ParseManifest(default, "alpha", null).SourceUrl);
        Assert.Equal("Ann", PluginData.ParseManifest(Parse("""{ "plugins": [ { "name": "a", "author": "Ann" } ] }"""), "a", null).Author);
    }

    [Theory]
    [InlineData("""{ "source": "github", "repo": "o/r" }""", "https://github.com/o/r")]
    [InlineData("""{ "source": "github", "repo": "o/r", "ref": "v1", "path": "sub/dir" }""", "https://github.com/o/r/tree/v1/sub/dir")]
    [InlineData("""{ "source": "url", "url": "https://github.com/o/r.git", "sha": "abc" }""", "https://github.com/o/r")]
    [InlineData("""{ "source": "git-subdir", "url": "https://github.com/o/r.git", "path": "plugins/x", "ref": "v1.5" }""", "https://github.com/o/r/tree/v1.5/plugins/x")]
    [InlineData("""{ "source": "git", "url": "git@github.com:o/r.git" }""", "https://github.com/o/r")]
    [InlineData("""{ "source": "url", "url": "https://git.example.test/o/r.git", "path": "p" }""", "https://git.example.test/o/r")]
    [InlineData("""{ "source": "url", "url": "file:///C:/repo" }""", null)]
    [InlineData("""{ "source": "url", "url": "ssh://git@host/o/r" }""", null)]
    [InlineData("""{ "source": "npm", "package": "x" }""", null)]
    [InlineData("""{ "source": "github", "repo": "javascript:alert(1)" }""", null)]
    [InlineData("""42""", null)]
    public void The_source_url_comes_from_the_source_entry_and_only_http_s(string source, string? expected) =>
        Assert.Equal(expected, PluginData.SourceUrl(Parse(source), "https://github.com/m/market"));

    [Fact]
    public void A_relative_source_needs_a_github_marketplace()
    {
        var rel = Parse("\"./plugins/x\"");
        Assert.Equal("https://github.com/m/market/tree/HEAD/plugins/x", PluginData.SourceUrl(rel, "https://github.com/m/market"));
        Assert.Equal("https://github.com/m/market", PluginData.SourceUrl(Parse("\"./\""), "https://github.com/m/market"));
        Assert.Equal("https://git.example.test/m", PluginData.SourceUrl(rel, "https://git.example.test/m"));
        Assert.Null(PluginData.SourceUrl(rel, null));
    }

    [Fact]
    public void A_known_marketplace_gives_its_folder_and_repo_and_the_manifest_is_read_from_the_folder()
    {
        var known = Parse("""
            { "a": { "source": { "source": "github", "repo": "o/r" }, "installLocation": "C:\\m\\a" },
              "b": { "source": { "source": "directory", "path": "C:\\Local" }, "installLocation": "C:\\m\\b" } }
            """);
        Assert.Equal(("C:\\m\\a", "https://github.com/o/r"), PluginData.ParseKnownMarketplace(known, "a"));
        Assert.Equal(("C:\\m\\b", null), PluginData.ParseKnownMarketplace(known, "b"));
        Assert.Equal((null, null), PluginData.ParseKnownMarketplace(known, "none"));

        var dir = Directory.CreateTempSubdirectory("mk");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, ".claude-plugin"));
            File.WriteAllText(Path.Combine(dir.FullName, ".claude-plugin", "marketplace.json"), MarketplaceJson);
            Assert.Equal("MIT", PluginData.ReadManifest(dir.FullName, "alpha", null).License);
            Assert.Null(PluginData.ReadManifest(Path.Combine(dir.FullName, "nope"), "alpha", null).License);
            Assert.Null(PluginData.ReadManifest(null, "alpha", null).License);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Marketplaces_name_where_they_come_from()
    {
        var markets = PluginData.ParseMarketplaces(Parse("""
            [ { "name": "zz", "source": "directory", "path": "C:\\Plugins", "installLocation": "C:\\Plugins" },
              { "name": "official", "source": "github", "repo": "owner/repo", "installLocation": "C:\\cache" },
              { "name": "odd", "source": "git", "url": "https://example.test/m.git" } ]
            """));

        Assert.Equal(["odd", "official", "zz"], markets.Select(m => m.Name));
        Assert.Equal(["git · https://example.test/m.git", "GitHub · owner/repo", @"Folder · C:\Plugins"], markets.Select(m => m.Origin));
    }

    [Fact]
    public void Details_read_the_description_the_inventory_and_the_cost()
    {
        var details = PluginData.ParseDetails("""
            demo 1.0.0
              Description: Does demo things.
              Source: demo@market

            Component inventory
              Skills (2)  one, two
              Agents (0)
              MCP servers (1)  demo-server  (tool schemas resolved at runtime; not counted)

            Projected token cost
              Always-on:   ~1.2k tok   added to every session

            Per-component (rounded)
              component              always-on  on-invoke
              one                         ~100      ~2.2k
            """);

        Assert.Equal("Does demo things.", details.Description);
        Assert.Equal([new PluginComponent("Skills", 2, "one, two"), new PluginComponent("Agents", 0, ""), new PluginComponent("MCP servers", 1, "demo-server")],
            details.Components);
        Assert.Equal("~1.2k tokens in every session", details.AlwaysOn);
        var none = PluginData.ParseDetails("error: plugin not found");
        Assert.Equal((null, null, 0), (none.Description, none.AlwaysOn, none.Components.Count));
        Assert.Empty(PluginData.ParseDetails("").Components);
    }

    [Fact]
    public void Options_come_from_the_schema_and_only_changes_are_saved()
    {
        var options = PluginData.ParseOptions(Parse("""
            { "schema": {
                "region": { "type": "string", "title": "Region", "options": ["eu", "us"], "required": true },
                "token": { "type": "string", "sensitive": true, "description": "API token" },
                "verbose": { "type": "boolean" },
                "folder": { "type": "string", "title": "" } },
              "inputs": { "region": "eu", "token": "", "verbose": "false", "folder": "C:\\Data" },
              "choices": { "region": ["eu", "us"], "verbose": ["true", "false"] },
              "configured": ["region", "token"], "unconfigured": [] }
            """));

        Assert.Equal(["region", "token", "verbose", "folder"], options.Select(o => o.Key));
        Assert.Equal(new PluginOption("token", "token", "API token", false, true, "", null, true), options[1]);
        Assert.Equal(("Region", true, true), (options[0].Title, options[0].Required, options[0].Choices!.SequenceEqual(["eu", "us"])));
        Assert.Equal("folder", options[3].Title); // an empty title falls back to the key

        var changes = PluginData.ChangedOptions([(options[0], "us"), (options[1], ""), (options[2], "false"), (options[3], " C:\\Other ")]);
        Assert.Equal(new Dictionary<string, string> { ["region"] = "us", ["folder"] = @"C:\Other" }, changes);
        Assert.Equal("s3cret", PluginData.ChangedOptions([(options[1], "s3cret")])["token"]);
        Assert.Empty(PluginData.ParseOptions(Parse("""{ "schema": {} }""")));
    }

    [Fact]
    public void Outcome_reads_the_last_json_line_and_the_command_to_confirm()
    {
        var outcome = PluginData.ParseOutcome("""
            Installing plugin "x"...
            {"command":"install","outcome":"failed","message":"Run it with --accept-command","failureCode":"command_source_unconfirmed","shownCommand":{"kind":"command_source","command":"npm i x","sha256":"abc123"}}
            """);

        Assert.Equal(new CliOutcome("Run it with --accept-command", "command_source_unconfirmed", "npm i x", "abc123"), outcome);
        Assert.Equal(new CliOutcome(null, null, null, null), PluginData.ParseOutcome("plain text only"));
        Assert.Equal(new CliOutcome(null, null, null, null), PluginData.ParseOutcome("{ not json"));
    }

    [Fact]
    public void Unexpected_json_gives_empty_lists()
    {
        Assert.Empty(PluginData.ParseInstalled(default));
        Assert.Empty(PluginData.ParseInstalled(Parse("""{ "installed": 3 }""")));
        Assert.Empty(PluginData.ParseCatalog(Parse("[]")));
        Assert.Empty(PluginData.ParseMarketplaces(Parse("{}")));
        Assert.Empty(PluginData.ParseOptions(Parse("[]")));
    }

    // ---- MCP ----

    [Fact]
    public void Mcp_list_lines_become_servers()
    {
        var servers = McpData.ParseList("""
            Checking MCP server health…

            claude.ai Notion: https://mcp.notion.example/mcp - √ Connected
            claude.ai CoinGecko : https://mcp.coins.example/mcp - ! Needs authentication
            plugin:ctx:ctx: https://mcp.ctx.example/mcp?client=plugin&token=x (HTTP) - ! Needs authentication
            plugin:pdf-viewer:pdf: npx -y @scope/server-pdf --stdio - √ Connected
            plugin:engineering:google calendar:  (HTTP) - - Not configured
            plugin:data:broken: https://api.broken.example/mcp (HTTP) - × Failed to connect — MCP endpoint not found at https://api.broken.example. Check the URL.
            mine: C:\Tools\server.exe --port 1 - ⏸ Pending approval
            """);

        Assert.Equal(7, servers.Count);
        Assert.Equal(new McpServer("claude.ai Notion", "https://mcp.notion.example/mcp", McpState.Connected, "Connected", null), servers[0]);
        Assert.Equal(("claude.ai CoinGecko ", "CoinGecko", true, McpState.NeedsAuth), (servers[1].Name, servers[1].Display, servers[1].IsConnector, servers[1].State));
        Assert.Equal(("https://mcp.ctx.example/mcp", "ctx", "ctx"), (servers[2].Endpoint, servers[2].Plugin, servers[2].Display)); // the query (a token) is cut
        Assert.Equal(("npx -y @scope/server-pdf --stdio", McpState.Connected), (servers[3].Endpoint, servers[3].State));
        Assert.Equal(("", McpState.NotSetUp, "google calendar"), (servers[4].Endpoint, servers[4].State, servers[4].Display));
        Assert.Equal((McpState.Failed, "MCP endpoint not found at https://api.broken.example. Check the URL."), (servers[5].State, servers[5].Error));
        Assert.Equal(("mine", null, McpState.Pending, @"C:\Tools\server.exe --port 1"), (servers[6].Display, servers[6].Plugin, servers[6].State, servers[6].Endpoint));
    }

    [Fact]
    public void Mcp_get_gives_one_fresh_status()
    {
        Assert.Equal((McpState.Connected, "Connected", (string?)null), McpData.StatusOf("claude.ai Notion:\n  Scope: claude.ai config\n  Status: √ Connected\n"));
        Assert.Equal((McpState.Failed, "Failed to connect", "no route"), McpData.StatusOf("x:\n  Status: × Failed to connect — no route\n  Type: http"));
        Assert.Null(McpData.StatusOf("No MCP server named \"x\"."));
    }

    [Fact]
    public void A_server_off_in_this_folder_is_disabled()
    {
        Assert.Equal(McpState.Disabled,
            Assert.Single(McpData.ParseList("claude.ai Gmail: https://gmail.example/mcp - ⊘ Disabled for this project (re-enable via /mcp)")).State);
        Assert.Equal(McpState.Disabled, McpData.StatusOf("x:\n  Status: ⊘ Disabled for this project (re-enable via /mcp)\n")?.State);
    }

    [Fact]
    public void Turning_a_server_off_lists_it_for_that_folder_only_and_on_again_unlists_it()
    {
        var root = JsonNode.Parse("""{"projects":{"C:/Work/Proj":{"allowedTools":[]},"C:/Work/Other":{"disabledMcpServers":["x"]}}}""")!.AsObject();
        string? Off(string folder) => root["projects"]![folder]!["disabledMcpServers"]?.ToJsonString();

        Assert.Null(McpData.SetOff(root, @"c:\work\proj\", "claude.ai Gmail ", off: true));
        Assert.Null(McpData.SetOff(root, @"C:\Work\Proj", "claude.ai Gmail ", off: true)); // already off: listed once
        Assert.Equal("""["claude.ai Gmail "]""", Off("C:/Work/Proj"));
        Assert.Null(McpData.SetOff(root, @"C:\Work\Proj", "claude.ai Gmail ", off: false));
        Assert.Equal("[]", Off("C:/Work/Proj"));
        Assert.Equal("""["x"]""", Off("C:/Work/Other"));
    }

    [Fact]
    public void Turning_on_a_server_that_is_on_changes_nothing_and_an_unknown_folder_is_refused()
    {
        var root = JsonNode.Parse("""{"projects":{"C:/Work/Proj":{}}}""")!.AsObject();
        Assert.Null(McpData.SetOff(root, @"C:\Work\Proj", "x", off: false));
        Assert.Equal("""{"projects":{"C:/Work/Proj":{}}}""", root.ToJsonString());
        Assert.Equal("Claude Code has no entry for this folder yet: start a session there once.", McpData.SetOff(root, @"C:\Elsewhere", "x", off: true));
    }

    [Fact]
    public void Add_server_builds_the_mcp_add_command()
    {
        Assert.Equal(["mcp", "add", "--transport", "http", "--scope", "local", "sentry", "https://mcp.sentry.example/mcp", "-H", "Authorization: Bearer t"],
            McpData.AddArgs(" sentry ", "http", "https://mcp.sentry.example/mcp", "Authorization: Bearer t\n\n", "local").Args);
        Assert.Equal(["mcp", "add", "--scope", "user", "files", "-e", "ROOT=C:\\Data", "-e", "MODE=ro", "--", "npx", "-y", "files-server", "--root", "C:\\My Data"],
            McpData.AddArgs("files", "stdio", "npx -y files-server --root \"C:\\My Data\"", "ROOT=C:\\Data\r\nMODE=ro", "user").Args);
    }

    [Theory]
    [InlineData("", "http", "https://x.example", "", "Give the server a name.")]
    [InlineData("has space", "http", "https://x.example", "", "Use letters, digits, '.', '-' or '_' in the name.")]
    [InlineData("x", "stdio", " ", "", "Enter the command to run.")]
    [InlineData("x", "sse", "not a url", "", "The URL must be absolute, like https://example.com/mcp.")]
    [InlineData("x", "stdio", "npx s", "NOEQUALS", "\"NOEQUALS\" is not KEY=value.")]
    [InlineData("x", "http", "https://x.example", "no colon", "\"no colon\" is not Name: value.")]
    public void Add_server_says_what_is_missing(string name, string transport, string target, string pairs, string error) =>
        Assert.Equal((null, error), McpData.AddArgs(name, transport, target, pairs, "local"));

    [Fact]
    public void Command_lines_split_on_spaces_outside_quotes() =>
        Assert.Equal(["a", "b c", "", "d"], McpData.SplitCommandLine("  a \"b c\" \"\" d "));

    [Fact]
    public void Mcp_servers_show_url_without_query_or_command_with_args()
    {
        var rows = McpData.ParseMcp(Json("claude.json").GetProperty("mcpServers"), "user");

        Assert.Equal([new McpRow("tool", "user", @"C:\Tools\tool.exe mcp --flag"), new McpRow("remote", "user", "https://example.test/mcp")], rows);
        Assert.Empty(McpData.ParseMcp(default, "user"));
    }

    [Theory]
    [InlineData(@"C:\Work\Proj", "local")]
    [InlineData(@"c:\work\proj\", "local")]
    [InlineData(@"C:\Work\Other", "other")]
    public void Project_servers_match_the_cwd_across_slashes_and_case(string cwd, string server)
    {
        var rows = McpData.ParseMcp(McpData.ProjectServers(Json("claude.json"), cwd), "local");
        Assert.Equal(server, Assert.Single(rows).Name);
    }

    [Fact]
    public void Project_servers_of_an_unknown_cwd_are_none() =>
        Assert.Empty(McpData.ParseMcp(McpData.ProjectServers(Json("claude.json"), @"C:\Elsewhere"), "local"));

    [Fact]
    public void Mcp_json_file_is_read_like_the_global_config() =>
        Assert.Equal(new McpRow("fromfile", "project", "npx -y pkg"), Assert.Single(McpData.ParseMcp(Json("mcp.json").GetProperty("mcpServers"), "project")));

    // ---- skills ----

    [Theory]
    [InlineData("---\nname: a\ndescription: Short one\n---\nbody", "a", "Short one")]
    [InlineData("---\nname: a\ndescription: >-\n  Folded one.\n  Second line.\nother: x\n---\n", "a", "Folded one. Second line.")]
    [InlineData("---\r\nname: \"q\"\r\ndescription: 'quoted'\r\n---\r\n", "q", "quoted")]
    [InlineData("---\nname: a\ndescription: plain\n  continued\n---", "a", "plain continued")]
    [InlineData("---\ndescription: only\n---", null, "only")]
    [InlineData("# no frontmatter\nname: x", null, null)]
    [InlineData("", null, null)]
    public void Frontmatter_reads_name_and_description(string text, string? name, string? description) =>
        Assert.Equal((name, description), PluginData.Frontmatter(text));

    [Fact]
    public void Skills_are_read_from_folders_with_a_folder_name_fallback()
    {
        var skills = PluginData.ReadSkills(Fixture("skills"), "user").OrderBy(s => s.Name).ToList();

        Assert.Equal([
            new SkillRow("alpha-skill", "Does alpha things. Use when alpha is needed.", "user"),
            new SkillRow("beta-skill", "Quoted beta.", "user"),
            new SkillRow("gamma", "", "user"),
        ], skills);
        Assert.Empty(PluginData.ReadSkills(Fixture("no-such-folder"), "user"));
    }

    // ---- the claude runner ----

    static readonly Account Acct = new("c", @"C:\nowhere", @"C:\nowhere\.claude.json", "t");
    static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    static Task<CliResult> Run(TimeSpan timeout, string? stdin = null, CancellationToken cancel = default, params string[] args) =>
        ClaudeCli.RunAsync(Acct, null, args, timeout, stdin, cancel, Cmd);

    [Fact]
    public async Task Cli_returns_stdout_stderr_and_the_exit_code()
    {
        var r = await Run(TimeSpan.FromSeconds(20), null, default, "/c", "echo", "out-ok", "&", "echo", "err-ok", "1>&2", "&", "exit", "3");
        Assert.Equal((3, "out-ok", "err-ok", false), (r.ExitCode, r.Output, r.Error, r.Ok));
        Assert.Equal("out-ok", r.Line);
    }

    [Fact]
    public async Task Cli_gives_up_after_the_timeout()
    {
        var r = await Run(TimeSpan.FromMilliseconds(300), null, default, "/c", "ping", "-n", "30", "127.0.0.1");
        Assert.True(r.TimedOut);
        Assert.Equal("Timed out.", r.Text);
    }

    [Fact]
    public async Task Cli_gives_up_when_a_child_keeps_the_output_open_after_exit()
    {
        // start /b leaves ping holding the inherited stdout pipe after cmd exits
        var r = await Run(TimeSpan.FromSeconds(1), null, default, "/c", "start", "/b", "ping", "-n", "15", "127.0.0.1", ">nul", "&", "echo", "done");
        Assert.True(r.TimedOut);
    }

    [Fact]
    public async Task Cli_passes_the_account_env() =>
        Assert.Equal(@"C:\nowhere", (await Run(TimeSpan.FromSeconds(20), null, default, "/c", "echo", "%CLAUDE_CONFIG_DIR%")).Output);

    [Fact]
    public async Task Cli_writes_stdin_then_closes_it_so_a_question_reads_end_of_input()
    {
        Assert.Equal("piped-in", (await Run(TimeSpan.FromSeconds(20), "piped-in", default, "/c", "more")).Output);
        // set /p would wait for a line forever on an open stdin
        Assert.EndsWith("answered", (await Run(TimeSpan.FromSeconds(20), null, default, "/c", "set", "/p", "x=Continue?", "&", "echo", "answered")).Output);
    }

    [Fact]
    public async Task Cli_cancel_kills_the_run()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(TimeSpan.FromSeconds(30), null, cancel.Token, "/c", "ping", "-n", "30", "127.0.0.1"));
    }

    [Fact]
    public void Cli_display_quotes_arguments_with_spaces() =>
        Assert.Equal("claude mcp login \"claude.ai CoinGecko \" \"\"", ClaudeCli.Display(["mcp", "login", "claude.ai CoinGecko ", ""]));
}
