using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CCWrapper.Core;

namespace CCWrapper.Data;

/// <param name="Source">built-in, user, project or a skill's "user skill" / "project skill".</param>
public sealed record SlashCommand(string Name, string Description, string Source);

/// <summary>The slash commands the compose box offers: Claude Code's built-ins, custom commands (<c>commands\**\*.md</c>) and
/// skills (<c>skills\*\SKILL.md</c>), each from the account's config folder and the project's <c>.claude</c> folder, and those
/// of the enabled plugins (<c>plugin:name</c>). shortcut: MCP prompts (<c>/mcp__server__prompt</c>) are left out, no file Claude
/// Code writes lists them.</summary>
public static class SlashCommands
{
    static SlashCommand B(string name, string description) => new(name, description, "built-in");

    // shortcut: hand-kept, Claude Code exposes no list to read; upgrade if it does.
    static readonly SlashCommand[] BuiltIn =
    [
        B("add-dir", "Add a working directory"), B("agents", "Manage subagents"), B("clear", "Clear the conversation"),
        B("color", "Set the session color"), B("compact", "Summarize the conversation to free context"), B("config", "Open settings"),
        B("context", "Show context usage"), B("cost", "Show token usage and cost"), B("doctor", "Check the Claude Code installation"),
        B("effort", "Set the effort level"), B("exit", "Exit Claude Code"), B("export", "Export the conversation"),
        B("help", "Show help and commands"), B("hooks", "Manage hooks"), B("ide", "Manage IDE integration"),
        B("init", "Create a CLAUDE.md for this project"), B("login", "Sign in"), B("logout", "Sign out"),
        B("mcp", "Manage MCP servers"), B("memory", "Edit memory files"), B("model", "Choose the model"),
        B("permissions", "Manage tool permissions"), B("plugin", "Manage plugins"), B("pr-comments", "Show comments of a pull request"),
        B("release-notes", "Show the release notes"), B("rename", "Rename the session"), B("resume", "Resume a conversation"),
        B("review", "Review a pull request"), B("rewind", "Rewind the conversation or code"), B("security-review", "Review pending changes for security"),
        B("skills", "List skills"), B("status", "Show version, model and account"), B("statusline", "Set up the status line"),
        B("terminal-setup", "Set up terminal key bindings"), B("theme", "Change the theme"), B("todos", "Show the to-do list"),
        B("usage", "Show plan usage"), B("vim", "Toggle vim editing mode"),
    ];

    /// <summary>Project entries win over user ones, those over built-ins (by name, case-insensitive); ordered by name.
    /// Missing folders and unreadable files are skipped. Reads files: call it off the UI thread.</summary>
    public static List<SlashCommand> List(Account account, string? cwd)
    {
        var found = new List<SlashCommand>();
        var roots = new List<(string Dir, string Source)>();
        if (cwd is { Length: > 0 }) roots.Add((Path.Combine(cwd, ".claude"), "project"));
        roots.Add((account.ConfigRoot, "user"));
        foreach (var (dir, source) in roots)
        {
            found.AddRange(Commands(Path.Combine(dir, "commands"), source));
            found.AddRange(Skills(Path.Combine(dir, "skills"), source + " skill", ""));
        }
        found.AddRange(Plugins(account, cwd));
        found.AddRange(BuiltIn);
        return found.Where(c => c.Name.Length > 0).DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Names starting with <paramref name="query"/> first, then those that only contain it, case-insensitive, each
    /// group in the given order. Empty query: all.</summary>
    public static List<SlashCommand> Filter(IEnumerable<SlashCommand> all, string query)
    {
        var rows = all.ToList();
        if (query.Length == 0) return rows;
        var starts = rows.Where(c => c.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return [.. starts, .. rows.Where(c => !starts.Contains(c) && c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))];
    }

    static IEnumerable<SlashCommand> Skills(string dir, string source, string prefix) =>
        PluginData.ReadSkills(dir, source).Where(s => s.UserInvocable).Select(s => new SlashCommand(prefix + s.Name, One(s.Description), s.Source));

    // The enabled plugins of installed_plugins.json: "enabledPlugins" of settings.json, project, then local settings, later wins.
    // Of several installs of one plugin the project one for this cwd wins, then the user one.
    static List<SlashCommand> Plugins(Account account, string? cwd)
    {
        var rows = new List<SlashCommand>();
        var enabled = new Dictionary<string, bool>();
        var settings = new List<string> { Path.Combine(account.ConfigRoot, "settings.json") };
        if (cwd is { Length: > 0 }) settings.AddRange([Path.Combine(cwd, ".claude", "settings.json"), Path.Combine(cwd, ".claude", "settings.local.json")]);
        foreach (var file in settings)
            foreach (var (id, on) in ReadJson(file, r => r.Prop("enabledPlugins") is { ValueKind: JsonValueKind.Object } e
                ? e.EnumerateObject().Select(p => (p.Name, p.Value.ValueKind == JsonValueKind.True)).ToList() : []) ?? [])
                enabled[id] = on;
        var installed = ReadJson(Path.Combine(account.ConfigRoot, "plugins", "installed_plugins.json"), r => r.Prop("plugins") is { ValueKind: JsonValueKind.Object } p
            ? p.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.Array && enabled.GetValueOrDefault(e.Name))
                .Select(e => (e.Name, Paths: e.Value.EnumerateArray()
                    .OrderBy(i => Same(i.Prop("projectPath").Str(), cwd) ? 0 : i.Prop("scope").Str() == "user" ? 1 : 2)
                    .Select(i => i.Prop("installPath").Str()).OfType<string>().ToList())).ToList() : []);
        foreach (var (id, paths) in installed ?? [])
        {
            var root = paths.FirstOrDefault(Directory.Exists);
            var name = id.LastIndexOf('@') is > 0 and var at ? id[..at] : id;
            if (root is null || name.Length == 0) continue;
            rows.AddRange(Commands(Path.Combine(root, "commands"), "plugin " + name, name + ":"));
            rows.AddRange(Skills(Path.Combine(root, "skills"), "plugin " + name, name + ":"));
        }
        rows.AddRange(Synced(account.ConfigRoot, enabled));
        return rows;
    }

    // claude.ai synced plugins: plugins\synced\<user id>\<name>[~gN]\ with <folder>.meta.json beside it, and a manifest.json. "~gN" is a
    // generation (plugin.json and the manifest name the plugin without it); the highest comes first and is the one kept.
    // shortcut: every synced plugin seen has installation_preference "available" and is offered, so only a known off-value or an
    // enabledPlugins false hides one; upgrade if Claude Code writes another vocabulary.
    static readonly string[] SyncedOff = ["disabled", "hidden", "unavailable", "uninstalled"];

    static List<SlashCommand> Synced(string configRoot, Dictionary<string, bool> enabled)
    {
        var rows = new List<SlashCommand>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var top = Path.Combine(configRoot, "plugins", "synced");
        try
        {
            if (!Directory.Exists(top)) return rows;
            foreach (var userDir in Directory.EnumerateDirectories(top))
            {
                var manifest = ReadJson(Path.Combine(userDir, "manifest.json"), r => r.Prop("plugins") is { ValueKind: JsonValueKind.Array } a
                    ? a.EnumerateArray().Select(e => (Id: e.Prop("pluginId").Str(), Name: e.Prop("name").Str(), Pref: e.Prop("installationPreference").Str(),
                        Market: e.Prop("marketplaceName").Str())).ToList() : []) ?? [];
                foreach (var dir in Directory.EnumerateDirectories(userDir).OrderByDescending(d => Regex.Match(d, @"~g(\d+)$") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0))
                {
                    var meta = ReadJson(dir + ".meta.json", r => (Id: r.Prop("server_plugin_id").Str(), Pref: r.Prop("installation_preference").Str(), Market: r.Prop("marketplace_name").Str()));
                    var entry = manifest.FirstOrDefault(e => e.Id is { Length: > 0 } && e.Id == meta.Id);
                    var name = ReadJson(Path.Combine(dir, ".claude-plugin", "plugin.json"), r => r.Prop("name").Str()) is { Length: > 0 } n ? n
                        : entry.Name is { Length: > 0 } ? entry.Name : Regex.Replace(Path.GetFileName(dir), @"~g\d+$", "");
                    var pref = meta.Pref ?? entry.Pref;
                    if (name.Length == 0 || !seen.Add(name) || SyncedOff.Contains(pref, StringComparer.OrdinalIgnoreCase)
                        || enabled.TryGetValue(name + "@" + (meta.Market ?? entry.Market), out var on) && !on) continue;
                    rows.AddRange(Commands(Path.Combine(dir, "commands"), "plugin " + name, name + ":"));
                    rows.AddRange(Skills(Path.Combine(dir, "skills"), "plugin " + name, name + ":"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // fewer synced plugins
        return rows;
    }

    static bool Same(string? a, string? b) => a is { Length: > 0 } && b is { Length: > 0 }
        && string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // null when the file is missing, unreadable or not JSON.
    static T? ReadJson<T>(string file, Func<JsonElement, T> read)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return read(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return default; }
    }

    // A subfolder becomes "dir:name", as Claude Code names it.
    static List<SlashCommand> Commands(string dir, string source, string prefix = "")
    {
        var rows = new List<SlashCommand>();
        try
        {
            if (!Directory.Exists(dir)) return rows;
            foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    if (!PluginData.UserInvocable(text)) continue;
                    var name = prefix + Path.ChangeExtension(Path.GetRelativePath(dir, file), null).Replace('\\', ':').Replace('/', ':');
                    rows.Add(new(name, Describe(text), source));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // this file is just missing
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // so are the rest of an unreadable folder
        return rows;
    }

    // The frontmatter description, else the first non-empty line below it (a markdown heading's # dropped).
    static string Describe(string markdown)
    {
        if (PluginData.Frontmatter(markdown).Description is { Length: > 0 } described) return One(described);
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var body = lines[0].Trim() == "---" ? Array.FindIndex(lines, 1, l => l.Trim() == "---") is >= 0 and var end ? end + 1 : lines.Length : 0;
        return One(lines.Skip(body).Select(l => l.Trim().TrimStart('#')).FirstOrDefault(l => l.Trim().Length > 0) ?? "");
    }

    static string One(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
