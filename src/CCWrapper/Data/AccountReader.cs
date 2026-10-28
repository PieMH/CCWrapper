using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CCWrapper.Core;

namespace CCWrapper.Data;

/// <summary>utilization is a percent (0-100), as Claude Code caches it.</summary>
public sealed record UsageWindow(double? Utilization, DateTimeOffset? ResetsAt);

/// <summary>What the picker and top bar show for one account. Every field is optional (null = unknown, show "—").</summary>
/// <param name="UsageFetchedAt">When Claude Code last cached the usage; can be days old.</param>
/// <param name="Model">settings.json "model", an alias like "opus" or a full id.</param>
/// <param name="Effort">modelSettings[model].effortLevel, else the top-level effortLevel.</param>
/// <param name="DefaultMode">settings.json permissions.defaultMode.</param>
public sealed record AccountInfo(
    string? Email, string? DisplayName, string? OrganizationName, string? OrganizationType, string? BillingType,
    DateTimeOffset? UsageFetchedAt, UsageWindow? FiveHour, UsageWindow? SevenDay,
    string? Model, string? Effort, string? DefaultMode);

/// <summary>One read-only row of the Account page's "other configuration": <paramref name="Source"/> is the file it came from.</summary>
public sealed record ConfigRow(string Source, string Key, string Value);

/// <summary>What the Account page shows beyond <see cref="AccountInfo"/>. Every field is optional.</summary>
public sealed record AccountFacts(string? AccountUuid, string? OrganizationUuid, string? InstallMethod, string? AutoUpdates,
    IReadOnlyList<ConfigRow> Other);

/// <summary>The browser pages of an account's own settings, chosen from its billing and organization type. One place for every URL.</summary>
public static class AccountLinks
{
    const string Claude = "https://claude.ai/settings/", ConsoleSite = "https://console.anthropic.com/settings/";

    /// <summary>Manage account, Usage and Billing as (label, https url). The Claude Console for API/console accounts, claude.ai
    /// for subscriptions and for any account of unknown kind (the fallback). Billing only when an account is signed in.</summary>
    public static List<(string Label, string Url)> For(AccountInfo info)
    {
        // organizationType of a subscription is "claude_pro", "claude_max", "claude_team", "claude_enterprise".
        var console = info.OrganizationType is { Length: > 0 } t ? !t.StartsWith("claude", StringComparison.OrdinalIgnoreCase)
            : info.BillingType is { } b && (b.Contains("api", StringComparison.OrdinalIgnoreCase) || b.Contains("prepaid", StringComparison.OrdinalIgnoreCase));
        var site = console ? ConsoleSite : Claude;
        var links = new List<(string, string)> { ("Manage account", console ? site + "profile" : site + "account"), ("Usage", site + "usage") };
        if (info.Email is not null || info.BillingType is not null) links.Add(("Billing", site + "billing"));
        return links;
    }
}

public static class AccountReader
{
    // Keys another Settings page edits (settings.json, or the leftovers of older versions in .claude.json), or that are noise.
    static readonly HashSet<string> Handled = new(StringComparer.Ordinal)
    {
        "model", "modelSettings", "effortLevel", "outputStyle", "language", "theme", "timeFormat", "autoUpdatesChannel", "attribution",
        "disableClaudeAiConnectors", "permissions", "hooks", "env", "autoCompactEnabled", "fileCheckpointingEnabled", "verbose",
        "showTurnDuration", "spinnerTipsEnabled", "prefersReducedMotion", "terminalProgressBarEnabled", "respectGitignore", "editorMode",
        "preferredNotifChannel", "installMethod", "autoUpdates", "userID", "anonymousId",
    };

    // Anything secret-like stays out, whatever the file or value.
    static readonly Regex Secret = new("key|token|secret|auth|credential|password", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The scalar preferences of .claude.json and the settings.json keys no other Settings page edits, secret-looking
    /// keys, caches and everything nested (projects, history, oauthAccount) left out. settings.json objects and arrays show as a size.</summary>
    public static List<ConfigRow> OtherConfig(JsonElement global, JsonElement settings) =>
        Rows(".claude.json", global, false).Concat(Rows("settings.json", settings, true)).ToList();

    static IEnumerable<ConfigRow> Rows(string source, JsonElement root, bool summarize)
    {
        if (root.ValueKind != JsonValueKind.Object) yield break;
        foreach (var p in root.EnumerateObject().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (Handled.Contains(p.Name) || Secret.IsMatch(p.Name) || p.Name.StartsWith("cached", StringComparison.Ordinal)) continue;
            var v = p.Value;
            var text = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() is { Length: > 120 } l ? l[..120] + "…" : v.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
                JsonValueKind.Object when summarize => $"{{{v.EnumerateObject().Count()} keys}}",
                JsonValueKind.Array when summarize => $"[{v.GetArrayLength()} items]",
                _ => null,
            };
            if (text is not null) yield return new(source, p.Name, text);
        }
    }

    /// <summary>The Account page's facts; off the UI thread like <see cref="Read"/>.</summary>
    public static AccountFacts ReadFacts(Account account)
    {
        using var global = JsonUtil.Load(account.GlobalConfigPath);
        using var settings = JsonUtil.Load(account.Paths.Settings);
        var g = global?.RootElement ?? default;
        var oauth = g.Prop("oauthAccount");
        var auto = g.Prop("autoUpdates");
        return new(oauth.Prop("accountUuid").Str(), oauth.Prop("organizationUuid").Str(), g.Prop("installMethod").Str(),
            auto.ValueKind is JsonValueKind.True or JsonValueKind.False ? auto.GetRawText() : null,
            OtherConfig(g, settings?.RootElement ?? default));
    }

    /// <summary>Reads .claude.json and settings.json; a missing or unparsable file just leaves its fields null.
    /// Blocks up to ~120 ms if a file is being rewritten, so call it off the UI thread.</summary>
    public static AccountInfo Read(Account account)
    {
        using var global = JsonUtil.Load(account.GlobalConfigPath);
        using var settings = JsonUtil.Load(account.Paths.Settings);
        var g = global?.RootElement ?? default;
        var s = settings?.RootElement ?? default;

        var oauth = g.Prop("oauthAccount");
        var usage = g.Prop("cachedUsageUtilization");
        var model = s.Prop("model").Str();
        return new AccountInfo(
            oauth.Prop("emailAddress").Str(), oauth.Prop("displayName").Str(),
            oauth.Prop("organizationName").Str(), oauth.Prop("organizationType").Str(), oauth.Prop("billingType").Str(),
            JsonUtil.Ms(usage.Prop("fetchedAtMs")),
            Window(usage.Prop("utilization").Prop("five_hour")), Window(usage.Prop("utilization").Prop("seven_day")),
            model, Effort(s, model), s.Prop("permissions").Prop("defaultMode").Str());
    }

    /// <summary>The "projects" keys of .claude.json with their lastStartTime (when present).</summary>
    internal static List<(string Path, DateTimeOffset? LastStart)> Projects(Account account)
    {
        using var global = JsonUtil.Load(account.GlobalConfigPath);
        var projects = (global?.RootElement ?? default).Prop("projects");
        return projects.ValueKind != JsonValueKind.Object ? []
            : projects.EnumerateObject().Select(p => (p.Name, JsonUtil.Ms(p.Value.Prop("lastStartTime")))).ToList();
    }

    static UsageWindow? Window(JsonElement w) => w.ValueKind != JsonValueKind.Object ? null
        : new(w.Prop("utilization").Num(),
            DateTimeOffset.TryParse(w.Prop("resets_at").Str(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null);

    static string? Effort(JsonElement settings, string? model)
    {
        // "model" is usually an alias ("opus") while modelSettings is keyed by full id ("claude-opus-5-5"): match by substring.
        var modelSettings = settings.Prop("modelSettings");
        if (model?.Split('[')[0] is { Length: > 0 } alias && modelSettings.ValueKind == JsonValueKind.Object)
            foreach (var m in modelSettings.EnumerateObject())
                if (m.Name.Contains(alias, StringComparison.OrdinalIgnoreCase) && m.Value.Prop("effortLevel").Str() is { } level)
                    return level;
        return settings.Prop("effortLevel").Str();
    }
}

/// <summary>Missing-is-default JSON access: Prop on anything that is not an object gives <c>default</c> (ValueKind Undefined).</summary>
internal static class JsonUtil
{
    public static JsonElement Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static string? Str(this JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    public static double? Num(this JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) ? d : null;

    public static DateTimeOffset? Ms(JsonElement e) =>
        e.Num() is > 0 and < 2.5e14 and var ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null;

    /// <summary>Parses a small JSON file Claude Code may be rewriting: retries a few times, then gives up with null.</summary>
    public static JsonDocument? Load(string path, int attempts = 4)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                using var stream = JsonlReader.OpenShared(path);
                return JsonDocument.Parse(stream);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                if (i >= attempts) return null;
                Thread.Sleep(40); // shortcut: blocking wait, fine for off-thread callers; make async if a UI thread ever calls this
            }
        }
    }
}
