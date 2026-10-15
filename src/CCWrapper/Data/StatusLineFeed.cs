using System.IO;
using CCWrapper.Core;

namespace CCWrapper.Data;

/// <summary>Feeds the relay's <c>live\&lt;TabId&gt;.json</c> into its session: StatusLine, plus SessionId, TranscriptPath
/// and Cwd when present. Values are set on the thread that created the feed (the UI thread). Dispose deletes the file.</summary>
public sealed class StatusLineFeed : IDisposable
{
    public static string LiveDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCWrapper", "live");

    readonly LiveSession _session;
    readonly string _path;
    readonly FileSystemWatcher _watcher;
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public StatusLineFeed(LiveSession session, string? dir = null)
    {
        _session = session;
        dir ??= LiveDir;
        Directory.CreateDirectory(dir);
        var name = session.TabId + ".json";
        _path = Path.Combine(dir, name);
        _watcher = new FileSystemWatcher(dir, name) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += OnChanged; // the relay's temp file is moved over the old one
        _watcher.EnableRaisingEvents = true;
    }

    void OnChanged(object sender, FileSystemEventArgs e)
    {
        string json;
        try
        {
            using var reader = new StreamReader(JsonlReader.OpenShared(_path)); // shared: never blocks the relay's move
            json = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // gone or replaced mid-read; the next update brings it back
        }
        if (StatusLineData.Parse(json) is not { } data) return;
        if (_ui is null) Apply(data);
        else _ui.Post(_ => Apply(data), null);
    }

    void Apply(StatusLineData data)
    {
        _session.StatusLine = data;
        if (data.SessionId is { } id) _session.SessionId = id;
        if (data.TranscriptPath is { } path) _session.TranscriptPath = path;
        if (data.Cwd is { } cwd) _session.Cwd = cwd;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a late relay write may hold it
    }
}
