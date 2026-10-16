using System.IO;
using System.Text.Json.Nodes;
using CCWrapper.Data;

namespace CCWrapper.Tests.Data;

/// <summary>The Settings window's files: what loads, what an edit changes, and when a save writes.</summary>
public sealed class SettingsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ccw-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string P(string name) => Path.Combine(_dir, name);
    string Backups => P("backups");

    const string UserJson = """
        {
          "permissions": {
            "defaultMode": "auto"
          },
          "model": "opus",
          "hooks": {
            "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "rtk hook claude && echo <ok>" } ] } ]
          },
          "unknownKey": { "kept": [1, 2.5, "x"] }
        }

        """;

    SettingsFile Write(string name, string text, SettingsScope scope = SettingsScope.User)
    {
        File.WriteAllText(P(name), text);
        return SettingsFile.Load(scope, P(name));
    }

    [Fact]
    public void A_missing_file_is_empty_and_saving_creates_it_with_its_folder()
    {
        var path = Path.Combine(_dir, "proj", ".claude", "settings.local.json");
        var file = SettingsFile.Load(SettingsScope.Local, path);
        Assert.Equal((null, null, 0, false), (file.LoadedText, file.Error, file.Pending.Count, file.Dirty));

        file.Set(["model"], "sonnet");
        Assert.Null(file.Save(Backups));

        Assert.Equal("{\n  \"model\": \"sonnet\"\n}\n", File.ReadAllText(path));
        Assert.False(file.Dirty);
        Assert.False(Directory.Exists(Backups)); // nothing to back up
    }

    [Fact]
    public void Saving_keeps_unknown_keys_order_and_text_and_backs_up_the_old_file()
    {
        var file = Write("settings.json", UserJson.Replace("\n", "\r\n"));
        file.Set(["permissions", "defaultMode"], "plan");
        file.Set(["timeFormat"], "24-hour");

        Assert.Null(file.Save(Backups));

        var saved = File.ReadAllText(P("settings.json"));
        Assert.Contains("\r\n", saved);
        Assert.DoesNotContain("\n\n", saved.Replace("\r\n", "\n").TrimEnd('\n')); // indented, not blank lines
        Assert.Contains("rtk hook claude && echo <ok>", saved); // no & escapes
        var root = JsonNode.Parse(saved)!.AsObject();
        Assert.Equal(["permissions", "model", "hooks", "unknownKey", "timeFormat"], root.Select(p => p.Key));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "kept": [1, 2.5, "x"] }"""), root["unknownKey"]));
        Assert.Equal("plan", (string?)root["permissions"]!["defaultMode"]);
        Assert.Equal(UserJson.Replace("\n", "\r\n"), File.ReadAllText(Path.Combine(Backups, Directory.GetFiles(Backups).Select(Path.GetFileName).Single()!)));
    }

    [Fact]
    public void An_unchanged_file_is_not_written()
    {
        var file = Write("settings.json", UserJson);
        file.Set(["model"], "opus"); // the same value
        var stamp = File.GetLastWriteTimeUtc(P("settings.json"));

        Assert.False(file.Dirty);
        Assert.Null(file.Save(Backups));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(P("settings.json")));
    }

    [Fact]
    public void A_file_changed_on_disk_since_loading_is_not_overwritten()
    {
        var file = Write("settings.json", UserJson);
        file.Set(["model"], "haiku");
        File.WriteAllText(P("settings.json"), UserJson.Replace("opus", "sonnet")); // Claude Code saved meanwhile

        Assert.NotNull(file.Save(Backups));
        Assert.Contains("sonnet", File.ReadAllText(P("settings.json")));
        Assert.True(file.Dirty);
        Assert.Empty(Directory.GetFiles(_dir, "*.ccwrapper-tmp"));
    }

    [Fact]
    public void Set_creates_the_objects_on_its_way_and_removing_prunes_the_empty_ones()
    {
        var file = Write("settings.json", """{ "attribution": { "commit": "" }, "permissions": { "defaultMode": "auto", "allow": ["Read"] } }""");

        file.Set(["modelSettings", "claude-opus-5-5", "effortLevel"], "high");
        Assert.Equal("high", (string?)file.Get(["modelSettings", "claude-opus-5-5", "effortLevel"]));

        file.Set(["modelSettings", "claude-opus-5-5", "effortLevel"], null);
        Assert.Null(file.Pending["modelSettings"]);
        file.Set(["permissions", "defaultMode"], null);
        Assert.NotNull(file.Pending["permissions"]); // still holds allow
        file.Set(["nothing", "here"], null); // removing what isn't there changes nothing
        Assert.Null(file.Pending["nothing"]);
        Assert.Equal("", (string?)file.Get(["attribution", "commit"])); // an empty string is a value
        Assert.True(JsonNode.DeepEquals(JsonValue.Create(""), file.GetSaved(["attribution", "commit"])));

        file.Revert();
        Assert.Equal("auto", (string?)file.Get(["permissions", "defaultMode"]));
        Assert.False(file.Dirty);
    }

    [Fact]
    public void A_file_that_is_not_json_is_kept_until_its_text_is_fixed()
    {
        var file = Write("settings.json", "{ \"model\": \"opus\", }");

        Assert.NotNull(file.Error);
        Assert.False(file.Dirty);
        Assert.Null(file.Save(Backups)); // nothing to write
        Assert.NotNull(file.ReplaceText("[1, 2]")); // not an object
        Assert.NotNull(file.ReplaceText("{ nope"));

        Assert.Null(file.ReplaceText("""{ "model": "opus" }"""));
        Assert.Null(file.Error);
        Assert.Null(file.Save(Backups));
        Assert.Equal("opus", (string?)JsonNode.Parse(File.ReadAllText(P("settings.json")))!["model"]);
    }

    [Fact]
    public void Source_follows_local_over_project_over_user()
    {
        var files = new Dictionary<SettingsScope, SettingsFile>
        {
            [SettingsScope.User] = Write("u.json", """{ "model": "opus", "theme": "dark" }"""),
            [SettingsScope.Project] = Write("p.json", """{ "model": "sonnet" }""", SettingsScope.Project),
            [SettingsScope.Local] = Write("l.json", "{}", SettingsScope.Local),
        };
        string Model(SettingsScope s) => SettingsData.Source(files, s, ["model"]) is var (k, from, v) ? $"{k} {from} {(string?)v}" : "";

        Assert.Equal("Overridden Project sonnet", Model(SettingsScope.User));
        Assert.Equal("SetHere Project sonnet", Model(SettingsScope.Project));
        Assert.Equal("Inherited Project sonnet", Model(SettingsScope.Local));
        Assert.Equal((SourceKind.Inherited, SettingsScope.User), SettingsData.Source(files, SettingsScope.Local, ["theme"]) is var (k, f, _) ? (k, f) : default);
        Assert.Equal(SourceKind.Default, SettingsData.Source(files, SettingsScope.User, ["language"]).Kind);
        var userOnly = new Dictionary<SettingsScope, SettingsFile> { [SettingsScope.User] = files[SettingsScope.User] };
        Assert.Equal(SourceKind.SetHere, SettingsData.Source(userOnly, SettingsScope.User, ["model"]).Kind);
    }

    [Theory]
    [InlineData("\"opus\"", "opus")]
    [InlineData("\"\"", "empty")]
    [InlineData("true", "true")]
    [InlineData("[1]", "[1]")]
    public void Values_show_strings_bare(string json, string shown) => Assert.Equal(shown, SettingsData.Show(JsonNode.Parse(json)));

    [Fact]
    public void Rules_are_set_without_duplicates_and_an_empty_list_goes_away()
    {
        var file = Write("settings.json", """{ "permissions": { "allow": ["Read"], "defaultMode": "auto" } }""");

        SettingsData.SetRules(file, "deny", ["Bash(git push:*)", "Bash(git push:*)", "Read(~/.ssh/**)"]);
        Assert.Equal(["Bash(git push:*)", "Read(~/.ssh/**)"], SettingsData.Rules(file.Pending, "deny"));
        SettingsData.SetRules(file, "allow", []);
        Assert.Null(file.Get(["permissions", "allow"]));
        Assert.Empty(SettingsData.Rules(file.Pending, "ask"));
    }

    [Fact]
    public void Env_is_set_whole_and_refused_when_a_name_is_bad_or_repeated()
    {
        var file = Write("settings.json", """{ "env": { "A": "1", "N": 5 } }""");
        Assert.Equal([("A", "1"), ("N", "5")], SettingsData.Env(file.Pending));

        Assert.Null(SettingsData.SetEnv(file, [("A", "2"), (" B_2 ", "x"), ("", "")])); // a blank row is ignored
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "A": "2", "B_2": "x" }"""), file.Pending["env"]));

        Assert.Equal("\"1X\" is not a variable name: letters, digits and _, not starting with a digit.", SettingsData.SetEnv(file, [("1X", "v")]));
        Assert.Equal("A variable has a value but no name.", SettingsData.SetEnv(file, [("", "v")]));
        Assert.Equal("A is there twice.", SettingsData.SetEnv(file, [("A", "1"), ("a", "2")]));
        Assert.Equal("2", (string?)file.Get(["env", "A"])); // refused: unchanged

        Assert.Null(SettingsData.SetEnv(file, []));
        Assert.Null(file.Pending["env"]);
    }

    [Theory]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("ANTHROPIC_API_KEY", true)]
    [InlineData("db_password", true)]
    [InlineData("BASH_DEFAULT_TIMEOUT_MS", false)]
    public void Secret_looking_names(string name, bool secret) => Assert.Equal(secret, SettingsData.LooksSecret(name));

    [Fact]
    public void Hooks_are_listed_added_by_matcher_and_removed_with_their_empty_groups()
    {
        var file = Write("settings.json", UserJson);
        var rows = SettingsData.Hooks(file.Pending);
        Assert.Equal(new HookRow("PreToolUse", 0, 0, "Bash", "command", "rtk hook claude && echo <ok>", null), Assert.Single(rows));

        SettingsData.AddHook(file, "PreToolUse", " Bash ", "second", 5);   // joins the Bash group
        SettingsData.AddHook(file, "PreToolUse", "Edit|Write", "third", null); // a new group
        SettingsData.AddHook(file, "UserPromptSubmit", "", "fourth", null);   // a new event, no matcher
        rows = SettingsData.Hooks(file.Pending);
        Assert.Equal([("PreToolUse", 0, 1, "Bash", "second", 5.0), ("PreToolUse", 1, 0, "Edit|Write", "third", null), ("UserPromptSubmit", 0, 0, null, "fourth", null)],
            rows.Skip(1).Select(r => (r.Event, r.Group, r.Index, r.Matcher, r.Command, r.Timeout)));
        Assert.Null(file.Get(["hooks", "UserPromptSubmit"])!.AsArray()[0]!["matcher"]);

        foreach (var row in SettingsData.Hooks(file.Pending).AsEnumerable().Reverse()) SettingsData.RemoveHook(file, row);
        Assert.Null(file.Pending["hooks"]);
    }

    [Fact]
    public void Text_files_ignore_line_ends_and_refuse_a_file_changed_meanwhile()
    {
        File.WriteAllText(P("CLAUDE.md"), "# Title\nline\n");
        var memory = TextFile.Load(P("CLAUDE.md"));
        memory.Pending = "# Title\r\nline\r\n"; // what a text box gives back
        Assert.False(memory.Dirty);

        memory.Pending = "# Title\r\nline\r\nmore\r\n";
        Assert.Null(memory.Save(Backups));
        Assert.Equal("# Title\nline\nmore\n", File.ReadAllText(P("CLAUDE.md")));

        memory.Pending = "changed";
        File.WriteAllText(P("CLAUDE.md"), "edited elsewhere");
        Assert.NotNull(memory.Save(Backups));
        Assert.Equal("edited elsewhere", File.ReadAllText(P("CLAUDE.md")));

        var fresh = TextFile.Load(P("CLAUDE.local.md"));
        Assert.Equal((null, "", false), (fresh.LoadedText, fresh.Pending, fresh.Dirty));
    }

    // ---- .claude.json ----

    [Fact]
    public void Claude_json_changes_under_its_lock_keep_the_rest_and_back_up_the_old_file()
    {
        var path = P(".claude.json");
        const string text = "{\n  \"numStartups\": 3,\n  \"projects\": {\n    \"C:/Work\": {\n      \"allowedTools\": []\n    }\n  },\n  \"oauthAccount\": {\n    \"emailAddress\": \"t@example.com\"\n  }\n}";
        File.WriteAllText(path, text); // no final newline, as Claude Code writes it

        Assert.Null(ClaudeJson.Update(path, root => McpData.SetOff(root, @"C:\Work", "s", off: true), Backups));

        Assert.Equal(text.Replace("\"allowedTools\": []", "\"allowedTools\": [],\n      \"disabledMcpServers\": [\n        \"s\"\n      ]"), File.ReadAllText(path));
        Assert.False(Directory.Exists(path + ".lock"));
        Assert.Equal(text, File.ReadAllText(Assert.Single(Directory.GetFiles(Backups))));
    }

    [Fact]
    public void Claude_json_waits_for_a_held_lock_and_takes_over_a_stale_one()
    {
        var path = P(".claude.json");
        File.WriteAllText(path, "{}");
        Directory.CreateDirectory(path + ".lock"); // a claude is writing
        static string? SetX(JsonObject root) { root["x"] = 1; return null; }

        Assert.Equal("Claude Code is writing its config file right now. Try again in a moment.", ClaudeJson.Update(path, SetX, Backups));
        Assert.Equal("{}", File.ReadAllText(path));
        Assert.True(Directory.Exists(path + ".lock")); // not ours to remove

        Directory.SetLastWriteTimeUtc(path + ".lock", DateTime.UtcNow.AddMinutes(-1)); // left by a claude that ended
        Assert.Null(ClaudeJson.Update(path, SetX, Backups));
        Assert.Equal("{\n  \"x\": 1\n}", File.ReadAllText(path));
        Assert.False(Directory.Exists(path + ".lock"));
    }

    [Fact]
    public void Claude_json_is_not_written_when_the_change_is_refused_or_changes_nothing_or_the_file_is_broken()
    {
        var path = P(".claude.json");
        File.WriteAllText(path, "{\"a\": 1}"); // indented unlike Claude Code: any write would show

        Assert.Equal("no", ClaudeJson.Update(path, _ => "no", Backups));
        Assert.Null(ClaudeJson.Update(path, _ => null, Backups));
        Assert.Equal("{\"a\": 1}", File.ReadAllText(path));

        File.WriteAllText(path, "{\"a\": ");
        Assert.StartsWith("Claude Code's config file is not valid JSON", ClaudeJson.Update(path, _ => null, Backups));
        Assert.False(Directory.Exists(Backups));
        Assert.False(Directory.Exists(path + ".lock"));
    }
}
