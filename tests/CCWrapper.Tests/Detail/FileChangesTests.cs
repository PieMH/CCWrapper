using System.IO;
using System.Text;
using System.Text.Json;
using CCWrapper.Core;
using CCWrapper.Data;

namespace CCWrapper.Tests.Detail;

public sealed class FileChangesTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("ccw-changes-").FullName;
    readonly string _cwd, _transcript, _history, _outside;
    readonly ClaudePaths _paths;

    public FileChangesTests()
    {
        _cwd = Directory.CreateDirectory(Path.Combine(_root, "proj")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        _paths = new ClaudePaths(Path.Combine(_root, "cfg"));
        _transcript = Path.Combine(_paths.ProjectDir(_cwd), "sess.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(_transcript)!);
        _history = Directory.CreateDirectory(Path.Combine(_paths.FileHistory, "sess")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    void Disk(string rel, string text) => File.WriteAllText(Path.Combine(_cwd, rel), text);
    void Backup(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(_history, name), bytes);
    void Backup(string name, string text) => Backup(name, Encoding.UTF8.GetBytes(text));

    void Write(params object[] records) =>
        File.AppendAllLines(_transcript, records.Select(r => JsonSerializer.Serialize(r)));

    static object Entry(string? backup, int version) => new { backupFileName = backup, version };
    static object Snapshot(Dictionary<string, object> tracked) =>
        new { type = "file-history-snapshot", messageId = "m", snapshot = new { trackedFileBackups = tracked }, isSnapshotUpdate = false };
    static object Delta(string path, string? backup, int version) =>
        new { type = "file-history-delta", trackingPath = path, backup = Entry(backup, version) };

    FileChanges Scenario()
    {
        Disk("new.txt", "a\nb\n");
        Disk("mod.txt", "one\n2\nthree\nfour\n");
        Disk("same.txt", "unchanged\n");
        Disk("lost.txt", "now\n");
        File.WriteAllBytes(Path.Combine(_cwd, "img.bin"), [1, 0, 2, 3]);
        File.WriteAllText(Path.Combine(_outside, "o.txt"), "o1\no2\n");
        Backup("h1@v1", "one\ntwo\nthree\n");
        Backup("h1@v2", "one\nTWO\nthree\n");   // a later checkpoint: must not become the base
        Backup("h2@v1", [1, 0, 2]);
        Backup("h3@v1", "x\ny\n");
        Backup("h5@v1", "unchanged\n");
        Backup("ho@v1", "o1\n");

        Write(Snapshot(new()
        {
            ["new.txt"] = Entry(null, 1),
            ["mod.txt"] = Entry("h1@v1", 1),
            ["img.bin"] = Entry("h2@v1", 1),
            ["gone.txt"] = Entry("h3@v1", 1),
            ["lost.txt"] = Entry("h4@v1", 1),   // h4@v1 was never written: backup cleaned up
            ["same.txt"] = Entry("h5@v1", 1),
            ["tmp.txt"] = Entry(null, 1),       // created, then removed again
            [Path.Combine(_outside, "o.txt")] = Entry("ho@v1", 1),
        }));
        Write(Delta("mod.txt", "h1@v2", 2));
        return new FileChanges(_paths, _transcript, _cwd);
    }

    static FileChange Find(IReadOnlyList<FileChange> all, string rel) => all.Single(c => c.RelPath == rel);

    [Fact]
    public void New_file_has_an_empty_base_and_counts_all_lines_as_added()
    {
        var c = Find(Scenario().Refresh(), "new.txt");

        Assert.Equal(ChangeKind.Added, c.Kind);
        Assert.False(c.IsBinary);
        Assert.Equal(2, c.Added);
        Assert.Equal(0, c.Removed);
        Assert.Equal("", c.BaseText());
        Assert.Equal("a\nb\n", c.CurrentText());
    }

    [Fact]
    public void Modified_file_diffs_the_earliest_backup_against_disk()
    {
        var c = Find(Scenario().Refresh(), "mod.txt");

        Assert.Equal(ChangeKind.Modified, c.Kind);
        Assert.Equal(2, c.Added);   // "2" and "four" in
        Assert.Equal(1, c.Removed); // "two" out
        Assert.Equal("one\ntwo\nthree\n", c.BaseText());
        Assert.Equal("one\n2\nthree\nfour\n", c.CurrentText());
    }

    [Fact]
    public void Binary_file_reports_sizes_instead_of_lines()
    {
        var c = Find(Scenario().Refresh(), "img.bin");

        Assert.True(c.IsBinary);
        Assert.Null(c.Added);
        Assert.Null(c.Removed);
        Assert.Equal(3L, c.BaseSize);
        Assert.Equal(4L, c.CurrentSize);
        Assert.Null(c.BaseText());
        Assert.Null(c.CurrentText());
    }

    [Fact]
    public void Binary_compare_reads_a_file_that_another_process_has_open_for_writing()
    {
        var live = Path.Combine(_cwd, "live.bin");
        File.WriteAllBytes(live, [1, 0, 3]);
        Backup("hb@v1", [1, 0, 2]); // same size as the file, so the bytes are compared
        Write(Delta("live.bin", "hb@v1", 1));
        using var writer = new FileStream(live, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        var c = Find(new FileChanges(_paths, _transcript, _cwd).Refresh(), "live.bin");

        Assert.True(c.IsBinary);
        Assert.Equal(3L, c.CurrentSize);
    }

    [Fact]
    public void Deleted_file_is_a_missing_file_on_disk()
    {
        var c = Find(Scenario().Refresh(), "gone.txt");

        Assert.Equal(ChangeKind.Deleted, c.Kind);
        Assert.Equal(0, c.Added);
        Assert.Equal(2, c.Removed);
        Assert.Equal(4L, c.BaseSize);
        Assert.Equal(0L, c.CurrentSize);
        Assert.Equal("x\ny\n", c.BaseText());
        Assert.Equal("", c.CurrentText());
    }

    [Fact]
    public void Missing_backup_still_lists_the_file_without_counts()
    {
        var c = Find(Scenario().Refresh(), "lost.txt");

        Assert.Equal(ChangeKind.Modified, c.Kind);
        Assert.Null(c.Added);
        Assert.Null(c.Removed);
        Assert.Null(c.BaseSize);
        Assert.Equal(4L, c.CurrentSize);
        Assert.Null(c.BaseText());
        Assert.Equal("now\n", c.CurrentText());
    }

    [Fact]
    public void Unchanged_and_created_then_removed_files_are_left_out_and_files_outside_cwd_keep_their_full_path()
    {
        var all = Scenario().Refresh();

        Assert.DoesNotContain(all, c => c.RelPath is "same.txt" or "tmp.txt");
        Assert.Equal(Path.Combine(_outside, "o.txt"), Find(all, Path.Combine(_outside, "o.txt")).FullPath);
        Assert.Equal(["gone.txt", "img.bin", "lost.txt", "mod.txt", "new.txt"], all.Select(c => c.RelPath).Where(p => !Path.IsPathRooted(p)));
        Assert.Equal(6, all.Count);
    }

    [Fact]
    public void Refresh_picks_up_appended_records_and_disk_edits_and_survives_a_missing_transcript()
    {
        var changes = Scenario();
        Assert.Equal(2, Find(changes.Refresh(), "new.txt").Added);

        Disk("new.txt", "a\nb\nc\n");
        Disk("later.txt", "l1\n");
        Write(Delta("later.txt", null, 1));
        var after = changes.Refresh();
        Assert.Equal(3, Find(after, "new.txt").Added);
        Assert.Equal(1, Find(after, "later.txt").Added);

        File.Delete(_transcript);
        Assert.Equal(after.Count, changes.Refresh().Count);   // keeps what it knew
        Assert.Empty(new FileChanges(_paths, Path.Combine(_root, "none.jsonl"), _cwd).Refresh());
    }

    [Fact]
    public void Crlf_files_count_changed_lines_not_every_line()
    {
        Disk("crlf.txt", "a\r\nb\r\nC\r\n");
        Backup("hc@v1", "a\r\nb\r\nc\r\n");
        Write(Delta("crlf.txt", "hc@v1", 1));

        var c = Find(new FileChanges(_paths, _transcript, _cwd).Refresh(), "crlf.txt");

        Assert.Equal(1, c.Added);
        Assert.Equal(1, c.Removed);
    }
}
