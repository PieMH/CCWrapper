using System.IO;
using System.Text.Json.Nodes;
using CCWrapper.Terminal;

namespace CCWrapper.Core;

/// <summary>CCWrapper's own settings, <c>%LocalAppData%\CCWrapper\app.json</c>. Not Claude Code's files. A missing or broken
/// file reads as "never asked" and a failed write is dropped: a preference must not take the app down.</summary>
public static class AppPrefs
{
    public static string DefaultPath => Path.Combine(ClaudeLaunch.AppDataDir, "app.json");

    /// <summary>The <c>updateCheck</c> switch; null when it was never answered (or the file is unreadable).</summary>
    public static bool? ReadUpdateCheck(string? path = null)
    {
        try { return Load(path ?? DefaultPath)?["updateCheck"] is JsonValue v && v.TryGetValue(out bool b) ? b : null; }
        catch (Exception) { return null; }
    }

    public static void WriteUpdateCheck(bool on, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            JsonObject o;
            try { o = Load(path) ?? []; }
            catch (Exception) { o = []; } // a broken file is replaced
            o["updateCheck"] = on;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, o.ToJsonString());
        }
        catch (Exception) { }
    }

    static JsonObject? Load(string path) => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
}
