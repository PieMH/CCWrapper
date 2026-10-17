using System.IO;
using CCWrapper.Core;

namespace CCWrapper.Data;

/// <param name="Source">built-in, user, project or a skill's "user skill" / "project skill".</param>
public sealed record SlashCommand(string Name, string Description, string Source);

/// <summary>The slash commands the compose box offers: Claude Code's built-ins, custom commands (<c>commands\**\*.md</c>) and
/// skills (<c>skills\*\SKILL.md</c>), each from the account's config folder and the project's <c>.claude</c> folder.</summary>
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
            found.AddRange(PluginData.ReadSkills(Path.Combine(dir, "skills"), source + " skill")
                .Select(s => new SlashCommand(s.Name, One(s.Description), s.Source)));
        }
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

    // A subfolder becomes "dir:name", as Claude Code names it.
    static List<SlashCommand> Commands(string dir, string source)
    {
        var rows = new List<SlashCommand>();
        try
        {
            if (!Directory.Exists(dir)) return rows;
            foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
            {
                try
                {
                    var name = Path.ChangeExtension(Path.GetRelativePath(dir, file), null).Replace('\\', ':').Replace('/', ':');
                    rows.Add(new(name, Describe(File.ReadAllText(file)), source));
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
