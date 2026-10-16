using System.Collections;
using System.IO;
using System.Text.Json;
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

    public static List<string> Args(LaunchOptions o, string sessionId, string? relaySettings)
    {
        List<string> args = o.ResumeId is null ? ["--session-id", sessionId] : o.Fork ? ["--resume", o.ResumeId, "--fork-session"] : ["--resume", o.ResumeId];
        void Add(string flag, string? value) { if (!string.IsNullOrWhiteSpace(value)) args.AddRange([flag, value]); }
        Add("--model", o.Model);
        Add("--effort", o.Effort);
        Add("--permission-mode", o.PermissionMode);
        Add("-n", o.Name);
        Add("--settings", relaySettings);
        return args;
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
        // Unquoted with forward slashes so Git Bash and PowerShell both run it.
        // shortcut: breaks if the install path has spaces; switch to the 8.3 short path if that ever happens.
        return JsonSerializer.Serialize(new { statusLine = new { type = "command", command = relayExe.Replace('\\', '/') } });
    }

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
