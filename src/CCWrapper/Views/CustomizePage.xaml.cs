using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;

namespace CCWrapper.Views;

/// <summary>Plugins, skills and MCP servers of the window's account, and the buttons that hand over to Claude Code's own
/// /plugin and /mcp. Read-only: nothing here writes a settings file.</summary>
public partial class CustomizePage : UserControl
{
    const string ConnectorsUrl = "https://claude.ai/settings/connectors";
    bool _loaded;

    public CustomizePage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        if (_loaded || AppState.Current is not { } app) return;
        _loaded = true;
        Heading.Text = $"Customize · {app.Account.Letter.ToUpperInvariant()} · {app.Account.Label}";
        try
        {
            var cwd = app.ActiveSession?.Cwd;
            var info = await Task.Run(() => CustomizeData.Load(app.Account, cwd));
            PluginsHeader.Text = $"Plugins ({info.Plugins.Count(p => p.Enabled)} enabled of {info.Plugins.Count})";
            SkillsHeader.Text = $"Skills ({info.Skills.Count})";
            McpHeader.Text = $"MCP servers ({info.Mcp.Count})";
            Plugins.ItemsSource = info.Plugins;
            Skills.ItemsSource = info.Skills;
            Mcp.ItemsSource = info.Mcp;
        }
        catch (Exception ex)
        {
            _loaded = false; // the files are read-only to us and may be mid-rewrite: reopening retries
            Status.Text = "Could not read the account's files: " + ex.Message;
        }
    }

    void OnManagePlugins(object sender, RoutedEventArgs e) => _ = SendAsync("/plugin");

    void OnManageMcp(object sender, RoutedEventArgs e) => _ = SendAsync("/mcp");

    async Task SendAsync(string line)
    {
        var session = AppState.Current.ActiveSession;
        if (TuiInput.BlockedReason(session) is { } why) { Status.Text = why; return; }
        Status.Text = $"Sent {line}: continue in the terminal.";
        if (await TuiInput.SubmitAsync(session!, line)) Application.Current.MainWindow?.Activate();
    }

    void OnOpenConnectors(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(ConnectorsUrl) { UseShellExecute = true });

    async void OnLoadConnectors(object sender, RoutedEventArgs e)
    {
        var app = AppState.Current;
        LoadButton.IsEnabled = false;
        ConnectorOutput.Text = "Running claude mcp list…";
        try { ConnectorOutput.Text = await CustomizeData.McpListAsync(app.Account, app.ActiveSession?.Cwd, TimeSpan.FromSeconds(90)); }
        catch (Exception ex) { ConnectorOutput.Text = "Could not run claude: " + ex.Message; }
        finally { LoadButton.IsEnabled = true; }
    }
}

public sealed record PluginRow(string Name, string Marketplace, string? Version, string? Scope, string? InstallPath, bool Enabled)
{
    public string Detail => string.Join(" · ", new[] { Marketplace, Version is null ? null : "v" + Version, Scope }.Where(s => !string.IsNullOrEmpty(s)));
    public string State => Enabled ? "enabled" : "disabled";
}

public sealed record SkillRow(string Name, string Description, string Source);

/// <param name="Source">user, project, .mcp.json or "plugin &lt;name&gt;".</param>
/// <param name="Detail">The url, else the command with its arguments.</param>
public sealed record McpRow(string Name, string Source, string Detail);

public sealed record CustomizeInfo(IReadOnlyList<PluginRow> Plugins, IReadOnlyList<SkillRow> Skills, IReadOnlyList<McpRow> Mcp);

/// <summary>Reads the Customize page's data from Claude Code's files. The Parse* methods are pure; Load does the file IO.</summary>
public static class CustomizeData
{
    /// <summary>Everything for the page. Blocking: call off the UI thread. Missing files give empty lists.</summary>
    public static CustomizeInfo Load(Account account, string? cwd)
    {
        using var installed = JsonUtil.Load(Path.Combine(account.Paths.Plugins, "installed_plugins.json"));
        using var settings = JsonUtil.Load(account.Paths.Settings);
        using var global = JsonUtil.Load(account.GlobalConfigPath);
        var g = global?.RootElement ?? default;
        var plugins = ParsePlugins(installed?.RootElement ?? default, settings?.RootElement ?? default);
        var skills = ReadSkills(account.Paths.Skills, "user");
        var mcp = ParseMcp(g.Prop("mcpServers"), "user");
        if (cwd is not null)
        {
            mcp.AddRange(ParseMcp(ProjectServers(g, cwd), "project"));
            using var local = JsonUtil.Load(Path.Combine(cwd, ".mcp.json"));
            mcp.AddRange(ParseMcp((local?.RootElement ?? default).Prop("mcpServers"), ".mcp.json"));
        }
        foreach (var p in plugins.Where(p => p.Enabled && p.InstallPath is not null)) // a disabled plugin loads nothing
        {
            skills.AddRange(ReadSkills(Path.Combine(p.InstallPath!, "skills"), p.Name));
            using var pluginMcp = JsonUtil.Load(Path.Combine(p.InstallPath!, ".mcp.json"));
            mcp.AddRange(ParseMcp((pluginMcp?.RootElement ?? default).Prop("mcpServers"), "plugin " + p.Name));
        }
        return new(plugins, skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(), mcp);
    }

    /// <summary>installed_plugins.json {"plugins":{"name@marketplace":[{scope,installPath,version}]}} with the on/off state of
    /// settings.json "enabledPlugins". The user-scope install wins when a plugin is installed more than once.</summary>
    public static List<PluginRow> ParsePlugins(JsonElement installed, JsonElement settings)
    {
        var plugins = installed.Prop("plugins");
        if (plugins.ValueKind != JsonValueKind.Object) return [];
        var enabled = settings.Prop("enabledPlugins");
        return plugins.EnumerateObject()
            .Select(p =>
            {
                var installs = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().ToList() : [];
                var pick = installs.FirstOrDefault(i => i.Prop("scope").Str() == "user");
                if (pick.ValueKind != JsonValueKind.Object) pick = installs.FirstOrDefault();
                var at = p.Name.LastIndexOf('@');
                return new PluginRow(at > 0 ? p.Name[..at] : p.Name, at > 0 ? p.Name[(at + 1)..] : "",
                    pick.Prop("version").Str(), pick.Prop("scope").Str(), pick.Prop("installPath").Str(),
                    enabled.Prop(p.Name).ValueKind == JsonValueKind.True);
            })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
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

    /// <summary>projects[cwd].mcpServers of .claude.json. Its keys use '/', the session's cwd '\'.</summary>
    public static JsonElement ProjectServers(JsonElement globalConfig, string cwd)
    {
        static string Norm(string path) => path.Replace('/', '\\').TrimEnd('\\');
        var projects = globalConfig.Prop("projects");
        if (projects.ValueKind != JsonValueKind.Object) return default;
        foreach (var p in projects.EnumerateObject())
            if (string.Equals(Norm(p.Name), Norm(cwd), StringComparison.OrdinalIgnoreCase)) return p.Value.Prop("mcpServers");
        return default;
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

    /// <summary><c>claude mcp list</c> under the account's env, hidden: stdout and stderr, or a note after <paramref name="timeout"/>.
    /// The exe and arguments are parameters only so a test can run something quick.</summary>
    public static async Task<string> McpListAsync(Account account, string? cwd, TimeSpan timeout, string? exe = null, string args = "mcp list")
    {
        var psi = new ProcessStartInfo(exe ?? ClaudeLaunch.FindClaude(), args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, // claude writes UTF-8 (…, ✓, ✗)
            WorkingDirectory = cwd is not null && Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        psi.Environment.Clear();
        foreach (var (key, value) in ClaudeLaunch.Env(account, Guid.Empty, Environment.GetEnvironmentVariables())) psi.Environment[key] = value;
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return $"Timed out after {timeout.TotalSeconds:0} s.";
        }
        var text = Regex.Replace(await stdout + await stderr, @"\x1b\[[0-9;?]*[A-Za-z]", "").Trim();
        return text.Length > 0 ? text : "(no output)";
    }
}
