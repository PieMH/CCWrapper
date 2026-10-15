using System.Diagnostics;
using System.IO;
using CCWrapper.Core;

namespace CCWrapper.Tests.Data;

/// <summary>A throwaway home with ~\.claude (A) and ~\.claude-x accounts; synthetic data only.</summary>
sealed class FakeHome : IDisposable
{
    public string Home { get; } = Directory.CreateTempSubdirectory("ccw-data-").FullName;

    public void Dispose() => Directory.Delete(Home, recursive: true);

    /// <summary>Creates the account dir and its .claude.json, returns it as AccountStore.Discover would.</summary>
    public Account AddAccount(string letter, string globalJson = "{}")
    {
        var isA = letter == "a";
        var dir = Path.Combine(Home, isA ? ".claude" : ".claude-" + letter);
        Directory.CreateDirectory(dir);
        File.WriteAllText(isA ? Path.Combine(Home, ".claude.json") : Path.Combine(dir, ".claude.json"), globalJson);
        return AccountStore.Find(letter, Home)!;
    }

    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Data", "Fixtures", name);

    /// <summary>Copies a fixture to projects\&lt;encoded cwd&gt;\&lt;id&gt;.jsonl and sets its mtime.</summary>
    public string AddTranscript(Account account, string projectCwd, string id, string fixture, DateTime? mtimeUtc = null)
    {
        var path = account.Paths.Transcript(projectCwd, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Fixture(fixture), path, overwrite: true);
        File.SetLastWriteTimeUtc(path, mtimeUtc ?? DateTime.UtcNow);
        return path;
    }

    public static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Writes sessions\&lt;pid&gt;.json. procStart defaults to the real creation FILETIME of that pid, as Claude Code writes it.</summary>
    public static void AddRegistry(Account account, int pid, string sessionId, string status = "idle", string? procStart = null, string extra = "")
    {
        procStart ??= Process.GetProcessById(pid).StartTime.ToFileTimeUtc().ToString();
        WriteFile(Path.Combine(account.Paths.Sessions, pid + ".json"),
            $$"""{"pid":{{pid}},"sessionId":"{{sessionId}}","cwd":"C:\\Work\\Alpha","procStart":"{{procStart}}","kind":"interactive","name":"n","status":"{{status}}","updatedAt":1792046000000{{extra}}}""");
    }

    /// <summary>A pid that was alive a moment ago and is gone now.</summary>
    public static int DeadPid()
    {
        using var p = Process.Start(new ProcessStartInfo("cmd", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        return p.Id;
    }
}
