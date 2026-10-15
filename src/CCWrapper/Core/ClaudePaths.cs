using System.IO;
using System.Text.RegularExpressions;

namespace CCWrapper.Core;

/// <summary>Where Claude Code keeps one account's files. Paths may not exist yet.</summary>
public sealed record ClaudePaths(string Root)
{
    public string Projects => Path.Combine(Root, "projects");
    public string Sessions => Path.Combine(Root, "sessions");
    public string FileHistory => Path.Combine(Root, "file-history");
    public string History => Path.Combine(Root, "history.jsonl");
    public string Settings => Path.Combine(Root, "settings.json");
    public string ClaudeMd => Path.Combine(Root, "CLAUDE.md");
    public string Plugins => Path.Combine(Root, "plugins");
    public string Skills => Path.Combine(Root, "skills");

    /// <summary>projects\&lt;encoded cwd&gt;, e.g. C:\Users\me\Proj -> C--Users-me-Proj.</summary>
    public string ProjectDir(string cwd) => Path.Combine(Projects, EncodeCwd(cwd));

    public string Transcript(string cwd, string sessionId) => Path.Combine(ProjectDir(cwd), sessionId + ".jsonl");

    // shortcut: CC also truncates very long (>200 char) names with a hash suffix; enumerate projects\ if that ever matters.
    public static string EncodeCwd(string cwd) => Regex.Replace(cwd, "[^a-zA-Z0-9]", "-");
}
