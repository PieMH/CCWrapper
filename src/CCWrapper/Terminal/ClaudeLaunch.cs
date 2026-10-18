using System.Collections;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CCWrapper.Core;

namespace CCWrapper.Terminal;

/// <summary>How to start one claude.exe tab. Optional flags are passed only when set, so the account's own defaults apply.</summary>
/// <param name="ResumeId">Null starts a new session with a fresh <c>--session-id</c>; otherwise <c>--resume &lt;id&gt;</c>.</param>
/// <param name="Fork">With <paramref name="ResumeId"/>: <c>--fork-session</c>, a copy under a new id that leaves the original alone.</param>
public sealed record LaunchOptions(string Cwd, string? ResumeId = null, string? Model = null, string? Effort = null,
    string? PermissionMode = null, string? Name = null, bool Fork = false);

/// <summary>Command line, environment and statusLine relay for claude.exe.</summary>
public static class ClaudeLaunch
{
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCWrapper");

    /// <summary>claude.exe from PATH, else the bare name (the spawn then fails with a visible error).</summary>
    public static string FindClaude() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim().Trim('"'), "claude.exe")).FirstOrDefault(File.Exists) ?? "claude.exe";

    /// <param name="browserMcp">From <see cref="BrowserMcpConfig"/>: adds the Browser tool and the line that tells Claude to use it.</param>
    public static List<string> Args(LaunchOptions o, string sessionId, string? relaySettings, string? browserMcp = null)
    {
        List<string> args = o.ResumeId is null ? ["--session-id", sessionId] : o.Fork ? ["--resume", o.ResumeId, "--fork-session"] : ["--resume", o.ResumeId];
        // --mcp-config takes any number of values: a flag must follow it, or it would swallow the next argument.
        if (browserMcp is not null) args.AddRange(["--mcp-config", browserMcp, "--append-system-prompt", BrowserPrompt]);
        void Add(string flag, string? value) { if (!string.IsNullOrWhiteSpace(value)) args.AddRange([flag, value]); }
        Add("--model", o.Model);
        Add("--effort", o.Effort);
        Add("--permission-mode", o.PermissionMode);
        Add("-n", o.Name);
        Add("--settings", relaySettings);
        return args;
    }

    public const string BrowserPrompt = "This session runs inside CCWrapper. To open, read or use web pages, use the mcp__ccwrapper-browser__* tools " +
        "(CCWrapper's Browser panel) instead of launching or driving another browser, unless the user names another browser.";

    /// <summary>The inline <c>--mcp-config</c> JSON that gives the tab the <c>ccwrapper-browser</c> MCP server: the relay exe
    /// in <c>--browser</c> mode, bridged to this window's <see cref="BrowserPipe"/>. Null when switched off in
    /// <see cref="AppPrefs"/> or when the relay exe is missing. Claude Code starts the command itself, without a shell, so
    /// the full path is safe with spaces.</summary>
    public static string? BrowserMcpConfig(string? relayExe = null, string? prefsPath = null)
    {
        if (!AppPrefs.ReadBrowserTool(prefsPath)) return null;
        relayExe ??= Path.Combine(AppContext.BaseDirectory, "CCWrapper.StatusRelay.exe");
        if (!File.Exists(relayExe)) return null;
        var server = new JsonObject
        {
            ["type"] = "stdio", ["command"] = relayExe, ["args"] = new JsonArray("--browser", BrowserPipe.NameFor(Environment.ProcessId)),
        };
        return new JsonObject { ["mcpServers"] = new JsonObject { ["ccwrapper-browser"] = server } }.ToJsonString();
    }

    // Set by a Claude Code session that started this app; a tab must not inherit them (the child would think it is nested in it).
    static readonly string[] ParentSessionVars =
    [
        "CLAUDECODE", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_EFFORT", "CLAUDE_CODE_SSE_PORT",
        "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_SESSION_ATTENDED", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_PID",
        "AI_AGENT", "CLAUDE_CODE_EXECPATH",
    ];

    /// <summary>The parent env copied minus the parent Claude Code session's variables, then the account's keys, then CCW_TAB and COLORTERM.</summary>
    public static Dictionary<string, string> Env(Account account, Guid tabId, IDictionary parent)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in parent) env[(string)e.Key] = e.Value as string ?? "";
        foreach (var name in ParentSessionVars) env.Remove(name);
        account.ApplyEnv(env);
        env["CCW_TAB"] = tabId.ToString();
        env["COLORTERM"] = "truecolor";
        return env;
    }

    /// <summary>The <c>--settings</c> JSON that installs the relay, or null when a statusLine is already configured or the relay
    /// exe is missing (e.g. quarantined): the session then starts without it. --settings outranks user and project settings,
    /// so any of them having one means the relay must stay out. It is passed inline, not as a file: nothing to write that
    /// could fail, go stale or be locked by an antivirus.</summary>
    public static string? RelaySettings(Account account, string cwd, string? relayExe = null)
    {
        string[] own = [account.Paths.Settings, Path.Combine(cwd, ".claude", "settings.json"), Path.Combine(cwd, ".claude", "settings.local.json")];
        if (own.Any(HasStatusLine)) return null;

        relayExe ??= Path.Combine(AppContext.BaseDirectory, "CCWrapper.StatusRelay.exe");
        if (!File.Exists(relayExe)) return null;
        // Unquoted with forward slashes so Git Bash and PowerShell both run it. Any character outside the safe set (a space,
        // ' & ( ) ; $ ` or a non-ASCII letter) would split or rewrite the command, so use the 8.3 short path; with 8.3 names
        // off for that volume, or a short name that still has such a character, the session starts without the relay.
        if (!IsShellSafe(relayExe))
        {
            relayExe = ShortPath(relayExe);
            if (relayExe is null) return null;
        }
        return JsonSerializer.Serialize(new { statusLine = new { type = "command", command = relayExe.Replace('\\', '/') } });
    }

    internal static bool IsShellSafe(string path) =>
        path.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or ':' or '\\' or '/' or '.' or '_' or '~' or '-');

    static string? ShortPath(string path)
    {
        var buffer = new char[32768];
        var n = GetShortPathNameW(path, buffer, buffer.Length);
        var short83 = n > 0 && n < buffer.Length ? new string(buffer, 0, (int)n) : null;
        return short83 is not null && IsShellSafe(short83) ? short83 : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint GetShortPathNameW(string path, char[] buffer, int size);

    // A raw match survives comments and half-written files; a false positive only skips the relay.
    static bool HasStatusLine(string settingsPath)
    {
        try
        {
            using var reader = new StreamReader(JsonlReader.OpenShared(settingsPath));
            return reader.ReadToEnd().Contains("\"statusLine\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // missing: no statusLine there
        }
    }
}
