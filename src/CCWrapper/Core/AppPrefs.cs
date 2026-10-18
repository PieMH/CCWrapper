using System.IO;
using System.Text.Json.Nodes;
using CCWrapper.Terminal;

namespace CCWrapper.Core;

/// <summary>CCWrapper's own settings, <c>%LocalAppData%\CCWrapper\app.json</c>. Not Claude Code's files. A missing or broken
/// file reads as "never asked". A failed write returns its message (null on success) and never throws: a preference must not take the app down.</summary>
public static class AppPrefs
{
    public static string DefaultPath => Path.Combine(ClaudeLaunch.AppDataDir, "app.json");

    /// <summary>The <c>updateCheck</c> switch; null when it was never answered (or the file is unreadable).</summary>
    public static bool? ReadUpdateCheck(string? path = null)
    {
        try { return Load(path ?? DefaultPath)?["updateCheck"] is JsonValue v && v.TryGetValue(out bool b) ? b : null; }
        catch (Exception) { return null; }
    }

    public static string? WriteUpdateCheck(bool on, string? path = null) => Write("updateCheck", on, path);

    /// <summary>The <c>browserTool</c> switch: Claude Code tabs get CCWrapper's Browser panel as an MCP tool. On unless
    /// switched off (missing or unreadable reads as on).</summary>
    public static bool ReadBrowserTool(string? path = null)
    {
        try { return Load(path ?? DefaultPath)?["browserTool"] is not JsonValue v || !v.TryGetValue(out bool b) || b; }
        catch (Exception) { return true; }
    }

    public static string? WriteBrowserTool(bool on, string? path = null) => Write("browserTool", on, path);

    static string? Write(string key, bool on, string? path)
    {
        path ??= DefaultPath;
        try
        {
            JsonObject o;
            try { o = Load(path) ?? []; }
            catch (Exception) { o = []; } // a broken file is replaced
            o[key] = on;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, o.ToJsonString());
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    static JsonObject? Load(string path) => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
}
