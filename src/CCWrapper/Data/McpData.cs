using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CCWrapper.Core;

namespace CCWrapper.Data;

public enum McpState { Unknown, Connected, NeedsAuth, Failed, NotSetUp, Pending, Disabled }

/// <summary>One line of <c>claude mcp list</c>. <see cref="Name"/> is exactly as claude prints it and is what its commands
/// take back ("claude.ai CoinGecko " ends in a space).</summary>
/// <param name="Endpoint">The url without its query (it may hold a token) or the command line; "" when none is set.</param>
public sealed record McpServer(string Name, string Endpoint, McpState State, string Status, string? Error)
{
    const string ConnectorPrefix = "claude.ai ";

    public bool IsConnector => Name.StartsWith(ConnectorPrefix);

    /// <summary>The plugin of a "plugin:&lt;plugin&gt;:&lt;server&gt;" name, else null.</summary>
    public string? Plugin => Name.StartsWith("plugin:") && Name.Split(':') is { Length: >= 3 } p ? p[1] : null;

    public string Display => IsConnector ? Name[ConnectorPrefix.Length..].Trim()
        : Plugin is { } plugin ? Name[("plugin:" + plugin + ":").Length..] : Name.Trim();
}

/// <param name="Source">user, project (this folder, in .claude.json), or .mcp.json.</param>
/// <param name="Detail">The url without its query, else the command with its arguments.</param>
public sealed record McpRow(string Name, string Source, string Detail);

/// <summary>Reads MCP servers: the user's own from Claude Code's files, and the health that <c>claude mcp list</c> prints.</summary>
public static class McpData
{
    /// <summary>Lines like "name: endpoint - √ Connected", "plugin:p:s: url (HTTP) - ! Needs authentication",
    /// "claude.ai X:  (HTTP) - - Not configured", "x: url - × Failed to connect — why" or "x: url - ⊘ Disabled for this
    /// project (re-enable via /mcp)". Other lines are skipped.</summary>
    public static List<McpServer> ParseList(string text)
    {
        var servers = new List<McpServer>();
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (Regex.Match(line.TrimEnd(), @"^(?<name>.+?): (?<ep>.*?)\s+-\s+(?<mark>\S+)\s+(?<status>.+)$") is not { Success: true } m) continue;
            var status = m.Groups["status"].Value.Trim();
            var state = StateOf(status);
            var error = state == McpState.Failed && status.IndexOf('—') is >= 0 and var dash ? status[(dash + 1)..].Trim() : null;
            var endpoint = Regex.Replace(m.Groups["ep"].Value.Trim(), @"\s*\((HTTP|SSE|stdio|WebSocket)\)$", "", RegexOptions.IgnoreCase);
            servers.Add(new(m.Groups["name"].Value, endpoint.Split('?')[0], state, state == McpState.Failed ? "Failed to connect" : status, error));
        }
        return servers;
    }

    /// <summary>The "Status: √ Connected" line of <c>claude mcp get &lt;name&gt;</c>, or null when it has none.</summary>
    public static (McpState State, string Status, string? Error)? StatusOf(string getOutput)
    {
        if (Regex.Match(getOutput, @"^\s*Status:\s+\S+\s+(?<status>.+)$", RegexOptions.Multiline) is not { Success: true } m) return null;
        var status = m.Groups["status"].Value.Trim();
        var state = StateOf(status);
        return state == McpState.Failed && status.IndexOf('—') is >= 0 and var dash
            ? (state, "Failed to connect", status[(dash + 1)..].Trim())
            : (state, status, null);
    }

    public static McpState StateOf(string status) =>
        status.StartsWith("Connected") ? McpState.Connected
        : status.StartsWith("Needs auth") ? McpState.NeedsAuth
        : status.StartsWith("Failed") ? McpState.Failed
        : status.StartsWith("Not configured") ? McpState.NotSetUp
        : status.StartsWith("Pending") ? McpState.Pending
        : status.StartsWith("Disabled") ? McpState.Disabled
        : McpState.Unknown;

    /// <summary>The user's own servers: user scope, this folder's in .claude.json (local scope), and its .mcp.json
    /// (project scope). Blocking file IO; missing files give none.</summary>
    public static List<McpRow> OwnServers(Account account, string? cwd)
    {
        using var global = JsonUtil.Load(account.GlobalConfigPath);
        var g = global?.RootElement ?? default;
        var rows = ParseMcp(g.Prop("mcpServers"), "user");
        if (cwd is null) return rows;
        rows.AddRange(ParseMcp(ProjectServers(g, cwd), "local"));
        using var shared = JsonUtil.Load(Path.Combine(cwd, ".mcp.json"));
        rows.AddRange(ParseMcp((shared?.RootElement ?? default).Prop("mcpServers"), "project"));
        return rows;
    }

    /// <summary>The "mcpServers" object → one row per server. Anything else gives no rows. A url's query string is cut: it may hold a token.</summary>
    public static List<McpRow> ParseMcp(JsonElement servers, string source)
    {
        if (servers.ValueKind != JsonValueKind.Object) return [];
        return servers.EnumerateObject().Select(s =>
        {
            var args = s.Value.Prop("args") is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().Select(x => x.Str()) : [];
            var detail = s.Value.Prop("url").Str()?.Split('?')[0] ?? string.Join(' ', args.Prepend(s.Value.Prop("command").Str()).Where(x => x is not null));
            return new McpRow(s.Name, source, detail);
        }).ToList();
    }

    /// <summary>projects[cwd].mcpServers of .claude.json.</summary>
    public static JsonElement ProjectServers(JsonElement globalConfig, string cwd)
    {
        var projects = globalConfig.Prop("projects");
        if (projects.ValueKind != JsonValueKind.Object) return default;
        foreach (var p in projects.EnumerateObject())
            if (SameFolder(p.Name, cwd)) return p.Value.Prop("mcpServers");
        return default;
    }

    /// <summary>Whether a "projects" key of .claude.json, which uses '/', names the folder <paramref name="cwd"/>.</summary>
    public static bool SameFolder(string key, string cwd)
    {
        static string Norm(string path) => path.Replace('/', '\\').TrimEnd('\\');
        return string.Equals(Norm(key), Norm(cwd), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Turns a server off in one folder, or back on, as /mcp disable and enable do: its name in
    /// projects[folder].disabledMcpServers of .claude.json (<paramref name="globalConfig"/>). Null when done, else why not.</summary>
    public static string? SetOff(JsonObject globalConfig, string cwd, string name, bool off)
    {
        if (globalConfig["projects"] is not JsonObject projects || projects.FirstOrDefault(p => SameFolder(p.Key, cwd)).Value is not JsonObject project)
            return "Claude Code has no entry for this folder yet: start a session there once.";
        bool Named(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s == name;
        var list = project["disabledMcpServers"] as JsonArray;
        if (off && list?.Any(Named) != true)
        {
            if (list is null) project["disabledMcpServers"] = list = [];
            list.Add(name);
        }
        if (!off && list is not null)
            foreach (var n in list.Where(Named).ToList()) list.Remove(n);
        return null;
    }

    /// <summary>The arguments of <c>claude mcp add</c> for the Add server form, or why not. <paramref name="transport"/> is
    /// stdio, http or sse; <paramref name="target"/> the command line (stdio) or the url; <paramref name="pairs"/> one
    /// KEY=value (stdio env) or "Name: value" (http/sse header) per line; <paramref name="scope"/> local, project or user.</summary>
    public static (List<string>? Args, string? Error) AddArgs(string name, string transport, string target, string pairs, string scope)
    {
        name = name.Trim();
        target = target.Trim();
        if (name.Length == 0) return (null, "Give the server a name.");
        if (!Regex.IsMatch(name, @"^[\w.-]+$")) return (null, "Use letters, digits, '.', '-' or '_' in the name.");
        if (target.Length == 0) return (null, transport == "stdio" ? "Enter the command to run." : "Enter the server's URL.");
        var stdio = transport == "stdio";
        if (!stdio && !Uri.TryCreate(target, UriKind.Absolute, out _)) return (null, "The URL must be absolute, like https://example.com/mcp.");
        var lines = pairs.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.FirstOrDefault(l => stdio ? !Regex.IsMatch(l, @"^[A-Za-z_][\w]*=") : !l.Contains(':')) is { } bad)
            return (null, stdio ? $"\"{bad}\" is not KEY=value." : $"\"{bad}\" is not Name: value.");

        List<string> args = stdio ? ["mcp", "add", "--scope", scope, name] : ["mcp", "add", "--transport", transport, "--scope", scope, name, target];
        foreach (var l in lines) args.AddRange([stdio ? "-e" : "-H", l]);
        if (stdio)
        {
            args.Add("--"); // everything after it is the server's own command line, flags included
            args.AddRange(SplitCommandLine(target));
        }
        return (args, null);
    }

    /// <summary>Splits on spaces outside double quotes; the quotes themselves are dropped.</summary>
    public static List<string> SplitCommandLine(string line)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; any = true; }
            else if (c == ' ' && !quoted) { if (any) parts.Add(current.ToString()); current.Clear(); any = false; }
            else { current.Append(c); any = true; }
        }
        if (any) parts.Add(current.ToString());
        return parts;
    }
}
