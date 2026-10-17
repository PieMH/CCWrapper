using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CCWrapper.Terminal;

namespace CCWrapper.Core;

/// <summary>The bottom bar's compact hint, kept in CCWrapper's own %LOCALAPPDATA%\CCWrapper\app.json under "compact". That
/// file also holds keys of other features: a save rewrites the whole object and leaves them as they are. A missing or
/// broken file, or a bad value, reads as the default.</summary>
public sealed record CompactPrefs(bool Enabled, int Threshold, string Prompt)
{
    public const string DefaultPrompt = "Keep: the current goal, decisions and constraints, files being changed, open tasks and unresolved errors. Drop: exploration output and resolved detours.";
    public static readonly CompactPrefs Default = new(true, 50, DefaultPrompt);

    public static string DefaultPath => Path.Combine(ClaudeLaunch.AppDataDir, "app.json");

    /// <summary>Raised after a successful <see cref="Save"/>, so open bars re-read.</summary>
    public static event Action? Saved;

    public static bool ValidThreshold(int t) => t is >= 1 and <= 99;

    /// <summary>What goes into the compose box: "/compact" and the prompt on one line (a newline would send it).</summary>
    public string Command => ("/compact " + Regex.Replace(Prompt, @"\s+", " ")).TrimEnd();

    public static CompactPrefs Load(string? path = null)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path ?? DefaultPath)) is not JsonObject { } root || root["compact"] is not JsonObject c) return Default;
            return new(c["enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) ? on : Default.Enabled,
                c["threshold"] is JsonValue t && t.TryGetValue<int>(out var n) && ValidThreshold(n) ? n : Default.Threshold,
                c["prompt"] is JsonValue p && p.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s : Default.Prompt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return Default; }
    }

    /// <summary>Null on success, else why it did not save (the file stays as it was; a broken file is not overwritten).</summary>
    public string? Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("app.json is not a JSON object") : [];
            root["compact"] = new JsonObject { ["enabled"] = Enabled, ["threshold"] = Threshold, ["prompt"] = Prompt };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return ex.Message;
        }
        Saved?.Invoke();
        return null;
    }
}
