using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CCWrapper.Data;

/// <param name="Scope">user, project, local, managed, or synced (from the claude.ai account).</param>
public sealed record PluginInstall(string Scope, string? Version, bool Enabled, string? ProjectPath, string? InstallPath);

/// <summary>One installed plugin with every install of its id: a plugin can be installed at user and project scope at once.</summary>
public sealed record PluginInfo(string Id, IReadOnlyList<PluginInstall> Installs, DateTimeOffset? InstalledAt, DateTimeOffset? Updated,
    IReadOnlyList<string> McpServers, IReadOnlyList<string> Notes)
{
    public string Name => Id.LastIndexOf('@') is > 0 and var at ? Id[..at] : Id;
    public string Marketplace => Id.LastIndexOf('@') is > 0 and var at ? Id[(at + 1)..] : "";

    /// <summary>Synced plugins come from the claude.ai account and are managed there.</summary>
    public bool Synced => Installs.All(i => i.Scope == "synced");

    /// <summary>The install the switch, Update and Uninstall act on: user scope first, then local, project, anything else.</summary>
    public PluginInstall Main => Installs.OrderBy(i => Array.IndexOf(ScopeOrder, i.Scope) is >= 0 and var r ? r : ScopeOrder.Length).First();

    public bool Enabled => Main.Enabled;
    public string Scopes => string.Join(" + ", Installs.Select(i => i.Scope).Distinct());

    static readonly string[] ScopeOrder = ["user", "local", "project"];
}

/// <summary>A plugin a marketplace offers (<c>claude plugin list --available --json</c>).</summary>
public sealed record CatalogEntry(string Id, string Name, string Description, string Marketplace, long Installs);

/// <param name="Source">github, directory, git, url…</param>
/// <param name="Where">The repo, url or folder it comes from.</param>
public sealed record MarketplaceInfo(string Name, string Source, string? Where)
{
    public string Origin => Source switch
    {
        "github" => $"GitHub · {Where}",
        "directory" => $"Folder · {Where}",
        _ => Where is null ? Source : $"{Source} · {Where}",
    };
}

/// <param name="Kind">Skills, Agents, Hooks, MCP servers, LSP servers…</param>
public sealed record PluginComponent(string Kind, int Count, string Names);

/// <summary>What <c>claude plugin details</c> prints: description, component inventory, always-on context cost.</summary>
public sealed record PluginDetails(string? Description, IReadOnlyList<PluginComponent> Components, string? AlwaysOn);

/// <summary>One option a plugin's manifest declares (<c>claude plugin configure --json</c>). Values are strings, booleans
/// "true"/"false". A sensitive option's value is never shown: <see cref="Value"/> is empty and <see cref="IsSet"/> says
/// whether one is stored.</summary>
public sealed record PluginOption(string Key, string Title, string? Description, bool Required, bool Sensitive, string Value,
    IReadOnlyList<string>? Choices, bool IsSet);

/// <summary>The result line of a <c>--json</c> plugin command. <see cref="Command"/> and <see cref="Sha256"/> are set when
/// the plugin installs by running a command its marketplace declares: a person must see it before
/// <c>--accept-command &lt;sha256&gt;</c> lets it run.</summary>
public sealed record CliOutcome(string? Message, string? FailureCode, string? Command, string? Sha256);

/// <param name="Source">"user", or the plugin the skill comes with.</param>
public sealed record SkillRow(string Name, string Description, string Source);

/// <summary>Parses what <c>claude plugin …</c> prints. Pure, except <see cref="ReadSkills"/>. Every field is optional:
/// these formats are Claude Code's own and change with its releases.</summary>
public static class PluginData
{
    /// <summary><c>claude plugin list --json</c> (an array) or <c>--available --json</c> (its "installed"), one entry per id,
    /// ordered by name.</summary>
    public static List<PluginInfo> ParseInstalled(JsonElement root)
    {
        var list = root.ValueKind == JsonValueKind.Array ? root : root.Prop("installed");
        if (list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray()
            .Where(e => e.Prop("id").Str() is { Length: > 0 })
            .GroupBy(e => e.Prop("id").Str()!)
            .Select(g => new PluginInfo(g.Key,
                g.Select(e => new PluginInstall(e.Prop("scope").Str() ?? "", e.Prop("version").Str(), e.Prop("enabled").ValueKind == JsonValueKind.True,
                    e.Prop("projectPath").Str(), e.Prop("installPath").Str())).ToList(),
                g.Select(e => Date(e.Prop("installedAt"))).Where(d => d is not null).Min(),
                g.Select(e => Date(e.Prop("lastUpdated"))).Where(d => d is not null).Max(),
                g.SelectMany(e => e.Prop("mcpServers") is { ValueKind: JsonValueKind.Object } m ? m.EnumerateObject().Select(p => p.Name) : []).Distinct().ToList(),
                g.SelectMany(e => Strings(e.Prop("notes"))).Distinct().ToList()))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The "available" list of <c>claude plugin list --available --json</c>.</summary>
    public static List<CatalogEntry> ParseCatalog(JsonElement root) =>
        root.Prop("available") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray()
                .Where(e => e.Prop("pluginId").Str() is { Length: > 0 })
                .Select(e => new CatalogEntry(e.Prop("pluginId").Str()!, e.Prop("name").Str() ?? e.Prop("pluginId").Str()!,
                    e.Prop("description").Str() ?? "", e.Prop("marketplaceName").Str() ?? "", (long)(e.Prop("installCount").Num() ?? 0)))
                .ToList()
            : [];

    /// <summary>Entries whose name or description holds <paramref name="query"/> (all when empty), most installed first,
    /// at most <paramref name="take"/>, and how many matched in all.</summary>
    public static (List<CatalogEntry> Shown, int Matches) Search(IEnumerable<CatalogEntry> catalog, string? query, string? marketplace, int take)
    {
        var q = query?.Trim() ?? "";
        var hits = catalog.Where(c => (marketplace is null || c.Marketplace == marketplace)
            && (q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(c => c.Installs).ToList();
        return (hits.Take(take).ToList(), hits.Count);
    }

    /// <summary><c>claude plugin marketplace list --json</c>, ordered by name.</summary>
    public static List<MarketplaceInfo> ParseMarketplaces(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
                .Where(e => e.Prop("name").Str() is { Length: > 0 })
                .Select(e => new MarketplaceInfo(e.Prop("name").Str()!, e.Prop("source").Str() ?? "",
                    e.Prop("repo").Str() ?? e.Prop("url").Str() ?? e.Prop("path").Str() ?? e.Prop("installLocation").Str()))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    /// <summary><c>claude plugin details</c>: "Description: …", the lines under "Component inventory" such as
    /// "Skills (7)  a, b" or "MCP servers (1)  x  (note)", and "Always-on: ~806 tok". Missing parts stay null or empty.</summary>
    public static PluginDetails ParseDetails(string text)
    {
        string? description = null, alwaysOn = null;
        var components = new List<PluginComponent>();
        var inInventory = false;
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Description:")) description ??= line["Description:".Length..].Trim();
            else if (line == "Component inventory") inInventory = true;
            else if (line.Length == 0 || !char.IsWhiteSpace(raw.FirstOrDefault())) inInventory = false;
            else if (inInventory && Regex.Match(line, @"^(?<kind>[A-Za-z][A-Za-z ]*?) \((?<n>\d+)\)\s*(?<names>.*)$") is { Success: true } m)
                components.Add(new(m.Groups["kind"].Value, int.Parse(m.Groups["n"].Value), Regex.Replace(m.Groups["names"].Value, @"\s+\(.*\)$", "").Trim()));
            if (Regex.Match(line, @"^Always-on:\s+(?<cost>~?[\d.,]+k?)\s*tok") is { Success: true } c) alwaysOn = c.Groups["cost"].Value + " tokens in every session";
        }
        return new(description, components, alwaysOn);
    }

    /// <summary><c>claude plugin configure &lt;id&gt; --json</c>: "schema" (key → title, description, required, sensitive),
    /// "inputs" (key → current value), "choices" (key → allowed values), "configured" (keys with a stored value).</summary>
    public static List<PluginOption> ParseOptions(JsonElement root)
    {
        if (root.Prop("schema") is not { ValueKind: JsonValueKind.Object } schema) return [];
        var configured = Strings(root.Prop("configured")).ToHashSet();
        return schema.EnumerateObject().Select(o =>
        {
            var choices = root.Prop("choices").Prop(o.Name) is { ValueKind: JsonValueKind.Array } c ? Strings(c).ToList() : null;
            return new PluginOption(o.Name, o.Value.Prop("title").Str() is { Length: > 0 } t ? t : o.Name, o.Value.Prop("description").Str(),
                o.Value.Prop("required").ValueKind == JsonValueKind.True, o.Value.Prop("sensitive").ValueKind == JsonValueKind.True,
                root.Prop("inputs").Prop(o.Name).Str() ?? "", choices, configured.Contains(o.Name));
        }).ToList();
    }

    /// <summary>What <c>claude plugin configure --values-stdin</c> gets: the options whose value changed. An empty sensitive
    /// field keeps the stored value (it is never shown, so empty means "not retyped").</summary>
    public static Dictionary<string, string> ChangedOptions(IEnumerable<(PluginOption Option, string Value)> fields) =>
        fields.Select(f => (f.Option, Value: f.Value.Trim()))
            .Where(f => f.Option.Sensitive ? f.Value.Length > 0 : f.Value != f.Option.Value)
            .ToDictionary(f => f.Option.Key, f => f.Value);

    /// <summary>The last JSON line of a <c>--json</c> plugin command's stdout, or all nulls when there is none.</summary>
    public static CliOutcome ParseOutcome(string stdout)
    {
        var line = stdout.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (line is null) return new(null, null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            var shown = r.Prop("shownCommand");
            return new(r.Prop("message").Str(), r.Prop("failureCode").Str(), shown.Prop("command").Str(), shown.Prop("sha256").Str());
        }
        catch (JsonException) { return new(null, null, null, null); }
    }

    /// <summary>One row per <c>&lt;dir&gt;\*\SKILL.md</c>: the frontmatter name and description (the folder name when there is none).</summary>
    public static List<SkillRow> ReadSkills(string dir, string source)
    {
        var rows = new List<SkillRow>();
        try
        {
            if (!Directory.Exists(dir)) return rows;
            foreach (var skillDir in Directory.EnumerateDirectories(dir))
            {
                var file = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(file)) continue;
                var (name, description) = Frontmatter(File.ReadAllText(file));
                rows.Add(new(name ?? Path.GetFileName(skillDir), description ?? "", source));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an unreadable skill folder just lists fewer skills
        }
        return rows;
    }

    /// <summary>name and description from a leading <c>---</c> block. Handles quotes, <c>&gt;</c>/<c>|</c> blocks and
    /// indented continuation lines. shortcut: no YAML escapes or flow syntax, upgrade to a YAML parser if skills need it.</summary>
    public static (string? Name, string? Description) Frontmatter(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        if (lines[0].Trim() != "---") return default;
        string? name = null, description = null;
        for (var i = 1; i < lines.Length && lines[i].Trim() != "---"; i++)
        {
            if (Regex.Match(lines[i], @"^(name|description):\s*(.*)$") is not { Success: true } m) continue;
            var parts = new List<string> { m.Groups[2].Value.Trim() };
            while (i + 1 < lines.Length && lines[i + 1].Length > 0 && char.IsWhiteSpace(lines[i + 1][0])) parts.Add(lines[++i].Trim());
            if (parts[0] is ">" or "|" or ">-" or "|-" or ">+" or "|+") parts.RemoveAt(0);
            var value = string.Join(' ', parts.Where(p => p.Length > 0)).Trim().Trim('"', '\'');
            if (m.Groups[1].Value == "name") name = value; else description = value;
        }
        return (name, description);
    }

    static DateTimeOffset? Date(JsonElement e) => e.Str() is { } s && DateTimeOffset.TryParse(s, out var d) ? d : null;

    static IEnumerable<string> Strings(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.Str()).OfType<string>() : [];
}
