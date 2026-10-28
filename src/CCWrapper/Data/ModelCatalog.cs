using System.IO;
using System.Text.Json;
using CCWrapper.Core;

namespace CCWrapper.Data;

/// <summary>One model of Claude Code's /model list. <paramref name="Alias"/> is its family ("opus"), the /model argument;
/// <paramref name="Name"/> the versioned name ("Opus 5.5"); <paramref name="Note"/> Claude Code's badge ("Requires usage credits").</summary>
public sealed record CatalogModel(string Id, string Alias, string Name, string? Note);

/// <summary>The model list Claude Code fetches for the signed-in account and caches in <c>cache\model-catalog</c>. Undocumented,
/// so every field is optional.</summary>
public static class ModelCatalog
{
    /// <summary>The main models of the newest cached catalog of the CLI (<c>*-cc.json</c>; <c>-ccd</c> is the desktop app's);
    /// empty when there is none (an API key, before the first start). Reads files: off the UI thread.</summary>
    public static IReadOnlyList<CatalogModel> Read(Account account)
    {
        IReadOnlyList<CatalogModel> best = [];
        double newest = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(account.ConfigRoot, "cache", "model-catalog"), "*-cc.json"))
            {
                using var doc = JsonUtil.Load(file);
                var root = doc?.RootElement ?? default;
                if (root.Prop("fetchedAt").Num() is not { } at || at <= newest) continue;
                if (Parse(root) is { Count: > 0 } models) (best, newest) = (models, at);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // no folder yet, or Claude Code rewriting it
        return best;
    }

    /// <summary>The "main" section's models, the first of each family; the older versions ("overflow") are left out.</summary>
    internal static List<CatalogModel> Parse(JsonElement root)
    {
        var list = new List<CatalogModel>();
        var models = root.Prop("catalog").Prop("config").Prop("models");
        if (models.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in models.EnumerateArray())
        {
            if (m.Prop("section").Str() != "main" || m.Prop("id").Str() is not { } id || m.Prop("name").Str() is not { } name) continue;
            var alias = m.Prop("runtime").Prop("family").Str() ?? m.Prop("short_name").Str()?.ToLowerInvariant();
            if (alias is { Length: > 0 } && !list.Any(x => x.Alias == alias))
                list.Add(new(id, alias, name, m.Prop("badge").Prop("message").Str()));
        }
        return list;
    }
}
