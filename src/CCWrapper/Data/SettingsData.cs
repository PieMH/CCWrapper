using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CCWrapper.Data;

/// <summary>The settings files of one account and folder, in precedence order: a later scope overrides an earlier one.</summary>
public enum SettingsScope { User, Project, Local }

public enum SourceKind { Default, Inherited, SetHere, Overridden }

/// <summary>A command hook of a settings file, flattened: hooks[Event][Group].hooks[Index].</summary>
public sealed record HookRow(string Event, int Group, int Index, string? Matcher, string Type, string? Command, double? Timeout);

/// <summary>Writes a file only when it still holds what was read: Claude Code (or the user) may have changed it meanwhile.</summary>
public static class SafeFile
{
    /// <summary>Null when written, else why not. <paramref name="expected"/> is the text read earlier (null: the file did not
    /// exist). The old file is copied to <paramref name="backupDir"/> first; the new text goes to a temp file next to it,
    /// then replaces it in one move, so a reader never sees half a file.</summary>
    public static string? Write(string path, string? expected, string text, string backupDir)
    {
        var temp = path + ".ccwrapper-tmp";
        try
        {
            var current = Read(path);
            if (current != expected) return "It changed on disk since it was opened. Reopen the window to see the new version.";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (current is not null)
            {
                Directory.CreateDirectory(backupDir);
                File.Copy(path, Path.Combine(backupDir, Regex.Replace(path, @"[:\\/]+", "_")), overwrite: true);
            }
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return ex.Message;
        }
    }

    /// <summary>The file's text, or null when it does not exist.</summary>
    public static string? Read(string path)
    {
        try
        {
            using var reader = new StreamReader(Core.JsonlReader.OpenShared(path));
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>One settings.json as loaded, and its pending edits. Saving writes the whole object back: keys this app does
/// not know stay as they were, in their order.</summary>
public sealed class SettingsFile
{
    static readonly JsonSerializerOptions Out = new() { WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    JsonObject _original = [];

    SettingsFile(SettingsScope scope, string path) => (Scope, Path) = (scope, path);

    public SettingsScope Scope { get; }
    public string Path { get; }

    /// <summary>The text on disk when loaded or last saved; null when there is no file yet.</summary>
    public string? LoadedText { get; private set; }

    /// <summary>Set when the file is not a JSON object: its keys can't be edited until its text is fixed (<see cref="ReplaceText"/>).</summary>
    public string? Error { get; private set; }

    public JsonObject Pending { get; private set; } = [];
    public bool Dirty => !JsonNode.DeepEquals(_original, Pending);

    /// <summary>Blocking file IO. A missing file is an empty one; an unreadable one is <see cref="Error"/>.</summary>
    public static SettingsFile Load(SettingsScope scope, string path)
    {
        var file = new SettingsFile(scope, path);
        try { file.LoadedText = SafeFile.Read(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { file.Error = ex.Message; return file; }
        if (file.LoadedText is { } text && text.Trim().Length > 0)
        {
            if (Parse(text, out var root) is { } error) file.Error = error;
            else file._original = root!;
        }
        file.Pending = (JsonObject)file._original.DeepClone();
        return file;
    }

    public JsonNode? Get(IReadOnlyList<string> path) => Walk(Pending, path);

    /// <summary>The value as loaded or last saved: a row whose value differs has an unsaved change.</summary>
    public JsonNode? GetSaved(IReadOnlyList<string> path) => Walk(_original, path);

    static JsonNode? Walk(JsonNode? node, IReadOnlyList<string> path)
    {
        foreach (var key in path) node = node is JsonObject o ? o[key] : null;
        return node;
    }

    /// <summary>Sets the value at <paramref name="path"/>, creating the objects on the way; null removes it, and then the
    /// objects it leaves empty.</summary>
    public void Set(IReadOnlyList<string> path, JsonNode? value)
    {
        var chain = new List<JsonObject> { Pending };
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (chain[^1][path[i]] is not JsonObject next)
            {
                if (value is null) return; // nothing there to remove
                chain[^1][path[i]] = next = [];
            }
            chain.Add(next);
        }
        if (value is not null) { chain[^1][path[^1]] = value; return; }
        chain[^1].Remove(path[^1]);
        for (var i = chain.Count - 1; i > 0 && chain[i].Count == 0; i--) chain[i - 1].Remove(path[i - 1]);
    }

    public void Revert() => Pending = (JsonObject)_original.DeepClone();

    /// <summary>What saving writes: the pending object, indented by 2 as Claude Code writes it, with the file's line ends.</summary>
    public string Text
    {
        get
        {
            var eol = LoadedText?.Contains("\r\n") == true ? "\r\n" : "\n";
            return Pending.ToJsonString(Out).ReplaceLineEndings(eol) + (LoadedText is null || LoadedText.EndsWith('\n') ? eol : "");
        }
    }

    /// <summary>The JSON page's text becomes the pending object. Null when it parsed, else why not (nothing changes then).</summary>
    public string? ReplaceText(string text)
    {
        if (Parse(text, out var root) is { } error) return error;
        Pending = root!;
        Error = null;
        return null;
    }

    /// <summary>Writes <see cref="Text"/> when there is a change. Null when saved or unchanged, else why not.</summary>
    public string? Save(string backupDir)
    {
        if (!Dirty) return null;
        var text = Text;
        if (SafeFile.Write(Path, LoadedText, text, backupDir) is { } error) return error;
        (LoadedText, _original) = (text, (JsonObject)Pending.DeepClone());
        return null;
    }

    static string? Parse(string text, out JsonObject? root)
    {
        root = null;
        try
        {
            root = JsonNode.Parse(text) as JsonObject;
            return root is null ? "The file is not a JSON object." : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return ex.Message;
        }
    }
}

/// <summary>A text file the Settings window edits whole: CLAUDE.md and CLAUDE.local.md.</summary>
public sealed class TextFile
{
    TextFile(string path, string? loaded) => (Path, LoadedText, Pending) = (path, loaded, loaded ?? "");

    public string Path { get; }
    public string? LoadedText { get; private set; }
    public string Pending { get; set; }

    /// <summary>Line ends don't count: a text box types \r\n into a \n file.</summary>
    public bool Dirty => Normalize(Pending) != Normalize(LoadedText ?? "");

    public static TextFile Load(string path) => new(path, SafeFile.Read(path));

    public string? Save(string backupDir)
    {
        if (!Dirty) return null;
        var text = Normalize(Pending);
        if (SafeFile.Write(Path, LoadedText, text, backupDir) is { } error) return error;
        LoadedText = text;
        return null;
    }

    public void Revert() => Pending = LoadedText ?? "";

    string Normalize(string text) => text.ReplaceLineEndings(LoadedText?.Contains("\r\n") == true ? "\r\n" : "\n");
}

/// <summary>Reads and edits the parts of settings.json the Settings window shows. Pure: works on the files' pending objects.</summary>
public static class SettingsData
{
    public static readonly SettingsScope[] Order = [SettingsScope.User, SettingsScope.Project, SettingsScope.Local];

    /// <summary>Where the value at <paramref name="path"/> comes from, seen from <paramref name="scope"/>'s file: a more
    /// specific file overrides it, this file sets it, a broader one does, or none does (Claude Code's default).</summary>
    public static (SourceKind Kind, SettingsScope? From, JsonNode? Value) Source(IReadOnlyDictionary<SettingsScope, SettingsFile> files,
        SettingsScope scope, IReadOnlyList<string> path)
    {
        JsonNode? At(SettingsScope s) => files.TryGetValue(s, out var f) ? f.Get(path) : null;
        var index = Array.IndexOf(Order, scope);
        foreach (var s in Enumerable.Reverse(Order[(index + 1)..]))
            if (At(s) is { } v) return (SourceKind.Overridden, s, v);
        if (At(scope) is { } mine) return (SourceKind.SetHere, scope, mine);
        foreach (var s in Enumerable.Reverse(Order[..index]))
            if (At(s) is { } v) return (SourceKind.Inherited, s, v);
        return (SourceKind.Default, null, null);
    }

    /// <summary>A value as the window shows it: strings bare ("" as "empty"), anything else as JSON.</summary>
    public static string Show(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) ? (s.Length == 0 ? "empty" : s) : value?.ToJsonString() ?? "—";

    public static List<string> Rules(JsonObject root, string kind) =>
        root["permissions"]?[kind] is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "").ToList() : [];

    /// <summary>permissions.&lt;kind&gt; becomes <paramref name="rules"/> (duplicates dropped); an empty list removes it.</summary>
    public static void SetRules(SettingsFile file, string kind, IEnumerable<string> rules)
    {
        var list = rules.Distinct().ToList();
        file.Set(["permissions", kind], list.Count == 0 ? null : new JsonArray(list.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()));
    }

    public static List<(string Name, string Value)> Env(JsonObject root) =>
        root["env"] is JsonObject env ? env.Select(p => (p.Key, p.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : p.Value?.ToJsonString() ?? "")).ToList() : [];

    /// <summary>"env" becomes <paramref name="vars"/>, or null and nothing changes when a name is invalid or repeated (the reason).</summary>
    public static string? SetEnv(SettingsFile file, IReadOnlyList<(string Name, string Value)> vars)
    {
        var named = vars.Where(v => v.Name.Trim().Length > 0 || v.Value.Length > 0).Select(v => (Name: v.Name.Trim(), v.Value)).ToList();
        if (named.FirstOrDefault(v => !Regex.IsMatch(v.Name, "^[A-Za-z_][A-Za-z0-9_]*$")) is { Name: var bad } && bad is not null)
            return bad.Length == 0 ? "A variable has a value but no name." : $"\"{bad}\" is not a variable name: letters, digits and _, not starting with a digit.";
        if (named.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
            return $"{twice.Key} is there twice.";
        var env = new JsonObject();
        foreach (var (name, value) in named) env[name] = value;
        file.Set(["env"], env.Count == 0 ? null : env);
        return null;
    }

    /// <summary>Names whose values are best not shown until clicked: API_KEY, GITHUB_TOKEN, DB_PASSWORD…</summary>
    public static bool LooksSecret(string name) => Regex.IsMatch(name, "KEY|TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIAL|AUTH", RegexOptions.IgnoreCase);

    public static List<HookRow> Hooks(JsonObject root)
    {
        var rows = new List<HookRow>();
        if (root["hooks"] is not JsonObject hooks) return rows;
        foreach (var (ev, groups) in hooks)
        {
            if (groups is not JsonArray list) continue;
            for (var g = 0; g < list.Count; g++)
            {
                if (list[g]?["hooks"] is not JsonArray entries) continue;
                var matcher = list[g]?["matcher"] is JsonValue m && m.TryGetValue<string>(out var ms) && ms.Length > 0 ? ms : null;
                for (var i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    rows.Add(new(ev, g, i, matcher, Str(e?["type"]) ?? "command", Str(e?["command"]),
                        e?["timeout"] is JsonValue t && t.TryGetValue<double>(out var secs) ? secs : null));
                }
            }
        }
        return rows;
    }

    /// <summary>Removes one hook, then the group and event it leaves empty.</summary>
    public static void RemoveHook(SettingsFile file, HookRow row)
    {
        if (file.Pending["hooks"]?[row.Event] is not JsonArray groups || row.Group >= groups.Count
            || groups[row.Group]?["hooks"] is not JsonArray entries || row.Index >= entries.Count) return;
        entries.RemoveAt(row.Index);
        if (entries.Count == 0) groups.RemoveAt(row.Group);
        if (groups.Count == 0) file.Set(["hooks", row.Event], null);
    }

    /// <summary>Adds a command hook to the group with the same matcher, else to a new group.</summary>
    public static void AddHook(SettingsFile file, string ev, string? matcher, string command, double? timeout)
    {
        matcher = string.IsNullOrWhiteSpace(matcher) ? null : matcher.Trim();
        if (file.Get(["hooks", ev]) is not JsonArray groups) file.Set(["hooks", ev], groups = []);
        var group = groups.OfType<JsonObject>().FirstOrDefault(g => (Str(g["matcher"]) is { Length: > 0 } m ? m : null) == matcher && g["hooks"] is JsonArray);
        if (group is null)
        {
            group = matcher is null ? new JsonObject { ["hooks"] = new JsonArray() } : new JsonObject { ["matcher"] = matcher, ["hooks"] = new JsonArray() };
            groups.Add(group);
        }
        var hook = new JsonObject { ["type"] = "command", ["command"] = command };
        if (timeout is { } t) hook["timeout"] = t;
        ((JsonArray)group["hooks"]!).Add(hook);
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
