using System.IO;
using System.Text.Json;
using CCWrapper.Terminal;

namespace CCWrapper.Core;

public sealed record Draft(string Text, int Caret);

/// <summary>The compose box's unsent text per Claude Code session, kept in memory while the app runs and written to
/// <c>%LocalAppData%\CCWrapper\drafts-&lt;letter&gt;.json</c> (one file per account, so windows of two accounts never clobber each other)
/// only by <see cref="SaveAll"/> (on exit, before an update restart, and when the app is deactivated while an update is ready to install). Keyed by the session id, which survives a resume; a tab that has none yet by its
/// tab id, and that draft moves to the session id once it appears. A missing or broken file reads as no drafts.
/// shortcut: two windows of the same account overwrite each other's file, last exit wins; merge on write if that happens.</summary>
public sealed class DraftStore(string path)
{
    public const int Cap = 100;
    const string TabPrefix = "tab:";
    static readonly Dictionary<string, DraftStore> Stores = []; // UI thread only

    readonly OrderedDictionary<string, Draft> _drafts = Read(path); // oldest edit first
    readonly Dictionary<Guid, (string Key, Draft? Draft)> _lastKey = []; // per tab: the key and draft it last wrote, so a changed session id (/clear, fork) leaves no copy behind

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
            if (_drafts.Remove(TabKey(s), out var byTab)) { _drafts[id] = byTab; return byTab; } // the tab got its id after the draft was stashed
            return null;
        }
        return _drafts.GetValueOrDefault(TabKey(s));
    }

    /// <summary>Stores the draft of <paramref name="s"/>; an empty one removes it.</summary>
    public void Set(LiveSession s, string text, int caret)
    {
        var tab = TabKey(s);
        _drafts.Remove(tab);
        var key = s.SessionId ?? tab;
        // Only the draft this tab wrote: another tab may have resumed the old id and edited it since.
        if (_lastKey.TryGetValue(s.TabId, out var old) && old.Key != key && _drafts.TryGetValue(old.Key, out var cur) && ReferenceEquals(cur, old.Draft)) _drafts.Remove(old.Key);
        _drafts.Remove(key);
        Draft? draft = text.Length > 0 ? new(text, caret) : null;
        if (draft is not null) _drafts[key] = draft;
        _lastKey[s.TabId] = (key, draft);
    }

    /// <summary>Atomic (temp file, then replace). Tab-id drafts are dropped (meaningless next run), the oldest beyond
    /// <see cref="Cap"/> too. Never throws: losing a draft must not take the app down on exit.</summary>
    public void Save()
    {
        try
        {
            var rows = _drafts.Where(d => !d.Key.StartsWith(TabPrefix) && d.Value.Text.Length > 0).TakeLast(Cap)
                .Select(d => new Row(d.Key, d.Value.Text, d.Value.Caret)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(rows));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception) { }
    }

    record Row(string Key, string Text, int Caret);

    static OrderedDictionary<string, Draft> Read(string path)
    {
        var drafts = new OrderedDictionary<string, Draft>();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(path)) is { } rows)
                foreach (var r in rows)
                    if (r is { Key.Length: > 0, Text.Length: > 0 }) drafts[r.Key] = new(r.Text, Math.Max(0, r.Caret));
        }
        catch (Exception) { } // broken file: no drafts
        return drafts;
    }
}
