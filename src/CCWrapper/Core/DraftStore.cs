using System.IO;
using System.Text.Json;
using CCWrapper.Terminal;

namespace CCWrapper.Core;

public sealed record Draft(string Text, int Caret);

/// <summary>The compose box's unsent text per Claude Code session, kept in memory while the app runs and written to
/// <c>%LocalAppData%\CCWrapper\drafts-&lt;letter&gt;.json</c> (one file per account, so windows of two accounts never clobber each other)
/// only by <see cref="SaveAll"/> (on exit, before an update restart, and when the app is deactivated while an update is ready to install). Keyed by the session id, which survives a resume; a tab that has none yet by its
/// tab id, and that draft moves to the session id once it appears. A draft not changed for <see cref="MaxAge"/> is dropped, and <see cref="Remove"/>
/// drops a deleted session's. A missing or broken file reads as no drafts.
/// shortcut: two windows of the same account overwrite each other's file, last exit wins; merge on write if that happens.</summary>
public sealed class DraftStore
{
    public const int Cap = 100;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    const string TabPrefix = "tab:";
    static readonly Dictionary<string, DraftStore> Stores = []; // UI thread only

    readonly string _path;
    readonly OrderedDictionary<string, Draft> _drafts = []; // oldest edit first
    readonly Dictionary<string, DateTime> _changed = []; // UTC of each draft's last change
    readonly Dictionary<Guid, (string Key, Draft? Draft)> _lastKey = []; // per tab: the key and draft it last wrote, so a changed session id (/clear, fork) leaves no copy behind

    public DraftStore(string path) { _path = path; Load(); }

    public static DraftStore For(Account account) => Stores.TryGetValue(account.Letter, out var s)
        ? s : Stores[account.Letter] = new(Path.Combine(ClaudeLaunch.AppDataDir, $"drafts-{account.Letter}.json"));

    /// <summary>Writes every store that was opened; call on exit.</summary>
    public static void SaveAll() { foreach (var s in Stores.Values) s.Save(); }

    static string TabKey(LiveSession s) => TabPrefix + s.TabId;

    public Draft? Get(LiveSession s)
    {
        if (s.SessionId is { } id)
        {
            if (_drafts.TryGetValue(id, out var d)) return d;
            if (_drafts.Remove(TabKey(s), out var byTab)) { _drafts[id] = byTab; _changed[id] = _changed.GetValueOrDefault(TabKey(s), DateTime.UtcNow); _changed.Remove(TabKey(s)); return byTab; } // the tab got its id after the draft was stashed
            return null;
        }
        return _drafts.GetValueOrDefault(TabKey(s));
    }

    /// <summary>Stores the draft of <paramref name="s"/>; an empty one removes it.</summary>
    public void Set(LiveSession s, string text, int caret)
    {
        var tab = TabKey(s);
        Drop(tab);
        var key = s.SessionId ?? tab;
        // Only the draft this tab wrote: another tab may have resumed the old id and edited it since.
        if (_lastKey.TryGetValue(s.TabId, out var old) && old.Key != key && _drafts.TryGetValue(old.Key, out var cur) && ReferenceEquals(cur, old.Draft)) Drop(old.Key);
        Drop(key);
        Draft? draft = text.Length > 0 ? new(text, caret) : null;
        if (draft is not null) { _drafts[key] = draft; _changed[key] = DateTime.UtcNow; }
        _lastKey[s.TabId] = (key, draft);
    }

    /// <summary>Drops the draft of a session that was deleted.</summary>
    public void Remove(string sessionId) => Drop(sessionId);

    void Drop(string key) { _drafts.Remove(key); _changed.Remove(key); }

    static bool Expired(DateTime changedUtc) => DateTime.UtcNow - changedUtc > MaxAge;

    /// <summary>Atomic (temp file, then replace). Tab-id drafts are dropped (meaningless next run), the oldest beyond
    /// <see cref="Cap"/> too. Never throws: losing a draft must not take the app down on exit.</summary>
    public void Save()
    {
        try
        {
            var rows = _drafts.Where(d => !d.Key.StartsWith(TabPrefix) && d.Value.Text.Length > 0 && !Expired(_changed.GetValueOrDefault(d.Key, DateTime.UtcNow))).TakeLast(Cap)
                .Select(d => new Row(d.Key, d.Value.Text, d.Value.Caret, _changed.GetValueOrDefault(d.Key, DateTime.UtcNow))).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(rows));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception) { }
    }

    record Row(string Key, string Text, int Caret, DateTime? Changed = null); // no Changed (an older file): as if changed now

    void Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(_path)) is { } rows)
                foreach (var r in rows)
                {
                    var at = r.Changed?.ToUniversalTime() ?? DateTime.UtcNow;
                    if (r is not { Key.Length: > 0, Text.Length: > 0 } || Expired(at)) continue;
                    _drafts[r.Key] = new(r.Text, Math.Max(0, r.Caret));
                    _changed[r.Key] = at;
                }
        }
        catch (Exception) { } // broken file: no drafts
    }
}
