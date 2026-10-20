using System.IO;
using CCWrapper.Core;

namespace CCWrapper.Tests;

public sealed class DraftStoreTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ccw-drafts-").FullName;
    string PathOf(string name) => System.IO.Path.Combine(_dir, name);
    static readonly Account Acc = new("c", @"C:\none\.claude-c", @"C:\none\.claude-c\.claude.json", "t@example.com");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static LiveSession Tab(string? id = null) => new(Acc) { SessionId = id };

    [Fact]
    public void Round_trips_through_the_file_only_on_save()
    {
        var path = PathOf("drafts-c.json");
        var s = Tab("abc");
        var store = new DraftStore(path);
        store.Set(s, "hello\nworld", 4);
        Assert.False(File.Exists(path)); // memory until Save

        store.Save();

        Assert.Equal(new Draft("hello\nworld", 4), new DraftStore(path).Get(s));
        Assert.Null(new DraftStore(path).Get(Tab("other")));
    }

    [Fact]
    public void Each_account_file_is_its_own()
    {
        var a = new DraftStore(PathOf("drafts-a.json")); var c = new DraftStore(PathOf("drafts-c.json"));
        a.Set(Tab("x"), "from a", 0);
        a.Save(); c.Save();
        Assert.Null(new DraftStore(PathOf("drafts-c.json")).Get(Tab("x")));
        Assert.NotNull(new DraftStore(PathOf("drafts-a.json")).Get(Tab("x")));
    }

    [Fact]
    public void Missing_or_broken_file_is_empty_and_replaced_on_save()
    {
        var path = PathOf("drafts-c.json");
        Assert.Null(new DraftStore(path).Get(Tab("x")));
        foreach (var junk in new[] { "{ not json", "{}", "[null,{\"Key\":\"x\"},{\"Key\":\"x\",\"Text\":\"\"}]" })
        {
            File.WriteAllText(path, junk);
            var store = new DraftStore(path);
            Assert.Null(store.Get(Tab("x")));
            store.Set(Tab("y"), "ok", 2);
            store.Save();
            Assert.Equal("ok", new DraftStore(path).Get(Tab("y"))!.Text);
        }
    }

    [Fact]
    public void Empty_text_drops_the_draft()
    {
        var s = Tab("x");
        var store = new DraftStore(PathOf("d.json"));
        store.Set(s, "text", 1);
        store.Set(s, "", 0);
        Assert.Null(store.Get(s));
    }

    [Fact]
    public void A_tab_draft_moves_to_the_session_id_and_is_not_saved_without_one()
    {
        var path = PathOf("d.json");
        var store = new DraftStore(path);
        var s = Tab();
        store.Set(s, "early", 5);
        s.SessionId = "late";
        Assert.Equal("early", store.Get(s)!.Text);

        var noId = Tab();
        store.Set(noId, "lost", 4);
        store.Save();
        var back = new DraftStore(path);
        Assert.Equal("early", back.Get(s)!.Text);
        Assert.Null(back.Get(noId));
    }

    [Fact]
    public void Save_keeps_the_newest_Cap_drafts()
    {
        var path = PathOf("d.json");
        var store = new DraftStore(path);
        for (var i = 0; i < DraftStore.Cap + 5; i++) store.Set(Tab($"s{i}"), "t", 0);
        store.Set(Tab("s0"), "touched", 0); // the oldest, edited again: now the newest
        store.Save();

        var back = new DraftStore(path);
        Assert.Null(back.Get(Tab("s1")));
        Assert.Null(back.Get(Tab("s5")));
        Assert.NotNull(back.Get(Tab("s6")));
        Assert.NotNull(back.Get(Tab("s0")));
        Assert.NotNull(back.Get(Tab($"s{DraftStore.Cap + 4}")));
    }
}
