using System.IO;
using CCWrapper.Data;

namespace CCWrapper.Tests.Data;

public sealed class SlashCommandsTests : IDisposable
{
    readonly FakeHome _home = new();
    readonly string _project;

    public SlashCommandsTests() => _project = Directory.CreateTempSubdirectory("ccw-proj-").FullName;

    public void Dispose()
    {
        _home.Dispose();
        Directory.Delete(_project, recursive: true);
    }

    static SlashCommand C(string name) => new(name, "", "t");

    [Fact]
    public void Filter_puts_prefix_matches_before_substring_matches_case_insensitively()
    {
        var all = new[] { C("review"), C("pr-comments"), C("compact"), C("security-review"), C("config") };
        Assert.Equal(["compact", "config", "pr-comments"], SlashCommands.Filter(all, "CO").Select(c => c.Name));
        Assert.Equal(["review", "security-review"], SlashCommands.Filter(all, "review").Select(c => c.Name));
        Assert.Equal(5, SlashCommands.Filter(all, "").Count);
        Assert.Empty(SlashCommands.Filter(all, "zzz"));
    }

    [Fact]
    public void List_reads_commands_and_skills_of_the_account_and_the_project()
    {
        var account = _home.AddAccount("x");
        var root = account.ConfigRoot;
        FakeHome.WriteFile(Path.Combine(root, "commands", "plain.md"), "\n# Do the plain thing\nbody\n");
        FakeHome.WriteFile(Path.Combine(root, "commands", "front.md"), "---\ndescription: \"With frontmatter\"\nmodel: opus\n---\nbody");
        FakeHome.WriteFile(Path.Combine(root, "commands", "nofront-desc.md"), "---\nmodel: opus\n---\n\nFirst   line\nsecond");
        FakeHome.WriteFile(Path.Combine(root, "commands", "git", "sub", "deep.md"), "Nested");
        FakeHome.WriteFile(Path.Combine(root, "skills", "pdf", "SKILL.md"), "---\nname: pdf\ndescription: Read PDFs\n---\n");
        FakeHome.WriteFile(Path.Combine(_project, ".claude", "commands", "proj.md"), "Project one");
        FakeHome.WriteFile(Path.Combine(_project, ".claude", "skills", "folder-name", "SKILL.md"), "no frontmatter");

        var list = SlashCommands.List(account, _project).ToDictionary(c => c.Name);

        Assert.Equal("Do the plain thing", list["plain"].Description);
        Assert.Equal("user", list["plain"].Source);
        Assert.Equal("With frontmatter", list["front"].Description);
        Assert.Equal("First line", list["nofront-desc"].Description);
        Assert.Equal("Nested", list["git:sub:deep"].Description);
        Assert.Equal("Read PDFs", list["pdf"].Description);
        Assert.Equal("Project one", list["proj"].Description);
        Assert.Equal("project", list["proj"].Source);
        Assert.Contains("folder-name", list.Keys);
        Assert.Contains("compact", list.Keys);
        Assert.Equal(list.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), list.Keys);
    }

    [Fact]
    public void List_tolerates_missing_folders_and_lets_project_then_user_win_over_built_ins()
    {
        var account = _home.AddAccount("x");
        var bare = SlashCommands.List(account, @"C:\does\not\exist");
        Assert.Contains(bare, c => c.Name == "compact" && c.Source == "built-in");
        Assert.Equal(bare.Count, SlashCommands.List(account, null).Count);

        FakeHome.WriteFile(Path.Combine(account.ConfigRoot, "commands", "compact.md"), "User compact");
        FakeHome.WriteFile(Path.Combine(_project, ".claude", "commands", "Compact.md"), "Project compact");
        FakeHome.WriteFile(Path.Combine(account.ConfigRoot, "commands", "init.md"), "User init");
        var list = SlashCommands.List(account, _project);
        Assert.Single(list, c => c.Name.Equals("compact", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Project compact", list.Single(c => c.Name.Equals("compact", StringComparison.OrdinalIgnoreCase)).Description);
        Assert.Equal("User init", list.Single(c => c.Name == "init").Description);
    }

    // A plugin folder as installed_plugins.json points at it: commands\ and skills\.
    static string Plugin(string root, string name, string version = "1.0.0")
    {
        var dir = Path.Combine(root, "plugins", "cache", name, version);
        FakeHome.WriteFile(Path.Combine(dir, "commands", "run.md"), "---\ndescription: Run it\n---\n");
        FakeHome.WriteFile(Path.Combine(dir, "commands", "sub", "deep.md"), "Deep one");
        FakeHome.WriteFile(Path.Combine(dir, "commands", "hidden.md"), "---\nuser-invocable: false\n---\nHidden");
        FakeHome.WriteFile(Path.Combine(dir, "skills", "review", "SKILL.md"), "---\nname: review\ndescription: Reviews\ndisable-model-invocation: true\n---\n");
        FakeHome.WriteFile(Path.Combine(dir, "skills", "internal", "SKILL.md"), "---\nname: internal\nuser-invocable: false\n---\n");
        return dir;
    }

    static string Installed(params (string Id, string Scope, string? ProjectPath, string Path)[] installs) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 2,
            plugins = installs.GroupBy(i => i.Id).ToDictionary(g => g.Key,
                g => g.Select(i => new { scope = i.Scope, projectPath = i.ProjectPath, installPath = i.Path }).ToArray()),
        });

    [Fact]
    public void List_adds_the_commands_and_skills_of_enabled_plugins_only()
    {
        var account = _home.AddAccount("x");
        var root = account.ConfigRoot;
        var on = Plugin(root, "on-plugin");
        var off = Plugin(root, "off-plugin");
        var unlisted = Plugin(root, "unlisted-plugin");
        FakeHome.WriteFile(Path.Combine(root, "plugins", "installed_plugins.json"),
            Installed(("on-plugin@mkt", "user", null, on), ("off-plugin@mkt", "user", null, off), ("unlisted-plugin@mkt", "user", null, unlisted)));
        FakeHome.WriteFile(Path.Combine(root, "settings.json"), """{"enabledPlugins":{"on-plugin@mkt":true,"off-plugin@mkt":false}}""");

        var list = SlashCommands.List(account, _project).ToDictionary(c => c.Name);

        Assert.Equal("Run it", list["on-plugin:run"].Description);
        Assert.Equal("plugin on-plugin", list["on-plugin:run"].Source);
        Assert.Contains("on-plugin:sub:deep", list.Keys);
        Assert.Equal("plugin on-plugin", list["on-plugin:review"].Source);
        Assert.DoesNotContain("on-plugin:hidden", list.Keys);
        Assert.DoesNotContain("on-plugin:internal", list.Keys);
        Assert.DoesNotContain(list.Keys, k => k.StartsWith("off-plugin:") || k.StartsWith("unlisted-plugin:"));
    }

    [Fact]
    public void A_local_setting_enables_a_plugin_and_the_project_install_wins_for_its_folder()
    {
        var account = _home.AddAccount("x");
        var root = account.ConfigRoot;
        var user = Plugin(root, "p", "1.0.0");
        var project = Plugin(root, "p", "2.0.0");
        FakeHome.WriteFile(Path.Combine(project, "commands", "only-in-project.md"), "x");
        FakeHome.WriteFile(Path.Combine(root, "plugins", "installed_plugins.json"),
            Installed(("p@mkt", "user", null, user), ("p@mkt", "project", _project, project)));
        FakeHome.WriteFile(Path.Combine(root, "settings.json"), """{"enabledPlugins":{"p@mkt":false}}""");
        Assert.DoesNotContain(SlashCommands.List(account, _project), c => c.Name.StartsWith("p:"));

        FakeHome.WriteFile(Path.Combine(_project, ".claude", "settings.local.json"), """{"enabledPlugins":{"p@mkt":true}}""");
        Assert.Contains(SlashCommands.List(account, _project), c => c.Name == "p:only-in-project");
        Assert.DoesNotContain(SlashCommands.List(account, null), c => c.Name.StartsWith("p:"));
        FakeHome.WriteFile(Path.Combine(root, "settings.json"), """{"enabledPlugins":{"p@mkt":true}}""");
        Assert.DoesNotContain(SlashCommands.List(account, null), c => c.Name == "p:only-in-project");
        Assert.Contains(SlashCommands.List(account, null), c => c.Name == "p:run");
    }

    [Fact]
    public void List_adds_claude_ai_synced_plugins_under_their_real_name()
    {
        var account = _home.AddAccount("x");
        var synced = Path.Combine(account.ConfigRoot, "plugins", "synced", "org_user");
        // "~g3" is a generation: the name comes from plugin.json, else the manifest, else the folder without the suffix.
        FakeHome.WriteFile(Path.Combine(synced, "eng~g3", "skills", "code-review", "SKILL.md"), "---\nname: code-review\ndescription: Review code\n---\n");
        FakeHome.WriteFile(Path.Combine(synced, "eng~g3", ".claude-plugin", "plugin.json"), """{"name":"engineering"}""");
        FakeHome.WriteFile(Path.Combine(synced, "eng~g3.meta.json"), """{"server_plugin_id":"p1","installation_preference":"available"}""");
        FakeHome.WriteFile(Path.Combine(synced, "eng~g2", "skills", "old", "SKILL.md"), "---\nname: old\n---\n");
        FakeHome.WriteFile(Path.Combine(synced, "eng~g2", ".claude-plugin", "plugin.json"), """{"name":"engineering"}""");
        FakeHome.WriteFile(Path.Combine(synced, "viewer", "commands", "open.md"), "Open a PDF");
        FakeHome.WriteFile(Path.Combine(synced, "viewer.meta.json"), """{"server_plugin_id":"p2"}""");
        FakeHome.WriteFile(Path.Combine(synced, "nometa~g2", "skills", "s", "SKILL.md"), "---\nname: s\n---\n");
        FakeHome.WriteFile(Path.Combine(synced, "off", "commands", "x.md"), "Off");
        FakeHome.WriteFile(Path.Combine(synced, "off.meta.json"), """{"installation_preference":"disabled"}""");
        FakeHome.WriteFile(Path.Combine(synced, "manifest.json"), """{"plugins":[{"pluginId":"p2","name":"pdf-viewer","installationPreference":"available"}]}""");

        var list = SlashCommands.List(account, null).ToDictionary(c => c.Name);

        Assert.Equal("plugin engineering", list["engineering:code-review"].Source);
        Assert.Equal("Review code", list["engineering:code-review"].Description);
        Assert.DoesNotContain("engineering:old", list.Keys); // the older generation is dropped, not merged
        Assert.Contains("pdf-viewer:open", list.Keys);
        Assert.Contains("nometa:s", list.Keys);
        Assert.DoesNotContain(list.Keys, k => k.StartsWith("off:") || k.Contains('~'));
    }

    [Fact]
    public void Synced_plugins_come_only_from_the_folder_of_the_current_login()
    {
        var account = _home.AddAccount("x", """{"oauthAccount":{"organizationUuid":"org1","accountUuid":"acc1"}}""");
        var synced = Path.Combine(account.ConfigRoot, "plugins", "synced");
        FakeHome.WriteFile(Path.Combine(synced, "org1_acc1", "now", "commands", "a.md"), "Current");
        FakeHome.WriteFile(Path.Combine(synced, "org0_old", "stale", "commands", "b.md"), "Old login");
        var list = SlashCommands.List(account, null).ToDictionary(c => c.Name);
        Assert.Contains("now:a", list.Keys);
        Assert.DoesNotContain("stale:b", list.Keys);

        // no ids in .claude.json: every folder, as before
        var other = _home.AddAccount("y", """{"oauthAccount":{"organizationUuid":"org1"}}""");
        FakeHome.WriteFile(Path.Combine(other.ConfigRoot, "plugins", "synced", "u1", "p", "commands", "a.md"), "A");
        FakeHome.WriteFile(Path.Combine(other.ConfigRoot, "plugins", "synced", "u2", "q", "commands", "b.md"), "B");
        var names = SlashCommands.List(other, null).Select(c => c.Name).ToList();
        Assert.Contains("p:a", names);
        Assert.Contains("q:b", names);
    }

    [Fact]
    public void An_installed_plugin_hides_a_synced_one_of_the_same_name()
    {
        var account = _home.AddAccount("x");
        var root = account.ConfigRoot;
        var dir = Plugin(root, "legal");
        FakeHome.WriteFile(Path.Combine(root, "plugins", "installed_plugins.json"), Installed(("legal@mkt", "user", null, dir)));
        FakeHome.WriteFile(Path.Combine(root, "settings.json"), """{"enabledPlugins":{"legal@mkt":true}}""");
        FakeHome.WriteFile(Path.Combine(root, "plugins", "synced", "u", "legal", "commands", "run.md"), "Synced run");
        Assert.Equal("Run it", SlashCommands.List(account, null).Single(c => c.Name == "legal:run").Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json {")]
    [InlineData("[]")]
    [InlineData("""{"plugins":{"a@m":"nope","b@m":[{"installPath":5},{}]}}""")]
    [InlineData("""{"plugins":{"a@m":[{"installPath":"C:\\no\\such\\folder"}]}}""")]
    public void List_survives_a_missing_or_garbled_installed_plugins_file(string? json)
    {
        var account = _home.AddAccount("x");
        FakeHome.WriteFile(Path.Combine(account.ConfigRoot, "settings.json"), """{"enabledPlugins":{"a@m":true,"b@m":true}}""");
        if (json is not null) FakeHome.WriteFile(Path.Combine(account.ConfigRoot, "plugins", "installed_plugins.json"), json);
        Assert.Equal(SlashCommands.List(_home.AddAccount("y"), null).Count, SlashCommands.List(account, null).Count);
    }
}
