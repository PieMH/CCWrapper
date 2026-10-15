using System.IO;
using System.Text.Json;

namespace CCWrapper.Core;

/// <summary>One Claude Code login. <see cref="ConfigDir"/> is null for A (~\.claude, CLAUDE_CONFIG_DIR unset).</summary>
/// <param name="Letter">Lower-case id from the folder name: "a" for ~\.claude, "x" for ~\.claude-x.</param>
/// <param name="GlobalConfigPath">The account's .claude.json (~\.claude.json for A).</param>
/// <param name="Label">oauthAccount.emailAddress, else "Account X".</param>
public sealed record Account(string Letter, string? ConfigDir, string GlobalConfigPath, string Label)
{
    public const string ConfigDirVar = "CLAUDE_CONFIG_DIR";

    /// <summary>The folder Claude Code uses for this account (A: ~\.claude, next to ~\.claude.json).</summary>
    public string ConfigRoot => ConfigDir ?? Path.Combine(Path.GetDirectoryName(GlobalConfigPath)!, ".claude");

    public ClaudePaths Paths => new(ConfigRoot);

    /// <summary>Sets CLAUDE_CONFIG_DIR for B/C, removes it (any casing) for A, on a child-process env block.</summary>
    public void ApplyEnv(IDictionary<string, string> env)
    {
        foreach (var key in env.Keys.Where(k => k.Equals(ConfigDirVar, StringComparison.OrdinalIgnoreCase)).ToList())
            env.Remove(key);
        if (ConfigDir is not null) env[ConfigDirVar] = ConfigDir;
    }
}

public static class AccountStore
{
    /// <summary>A = ~\.claude if it exists; x = each ~\.claude-x that holds a .claude.json. Sorted by letter.</summary>
    public static IReadOnlyList<Account> Discover(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var accounts = new List<Account>();
        if (Directory.Exists(Path.Combine(home, ".claude")))
            accounts.Add(Make("a", null, Path.Combine(home, ".claude.json")));
        foreach (var dir in Directory.EnumerateDirectories(home, ".claude-*"))
        {
            var global = Path.Combine(dir, ".claude.json");
            if (File.Exists(global))
                accounts.Add(Make(Path.GetFileName(dir)[".claude-".Length..].ToLowerInvariant(), dir, global));
        }
        return accounts.OrderBy(a => a.Letter, StringComparer.Ordinal).ToList();
    }

    public static Account? Find(string letter, string? home = null) =>
        Discover(home).FirstOrDefault(a => a.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase));

    static Account Make(string letter, string? dir, string global) =>
        new(letter, dir, global, ReadEmail(global) ?? $"Account {letter.ToUpperInvariant()}");

    static string? ReadEmail(string globalConfig)
    {
        try
        {
            using var stream = JsonlReader.OpenShared(globalConfig);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("oauthAccount", out var o) && o.ValueKind == JsonValueKind.Object
                && o.TryGetProperty("emailAddress", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // missing or mid-rewrite: fall back to "Account X"
        }
    }
}
