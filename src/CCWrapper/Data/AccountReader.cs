using System.Globalization;
using System.IO;
using System.Text.Json;
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

public static class AccountReader
{
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
