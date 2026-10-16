using System.Diagnostics;
using System.IO;
using CCWrapper.Data;

namespace CCWrapper.Tests.Data;

public sealed class GitInfoTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ccw-git-").FullName;

    public void Dispose()
    {
        foreach (var f in new DirectoryInfo(_dir).EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal; // .git objects are read-only
        Directory.Delete(_dir, recursive: true);
    }

    void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _dir, CreateNoWindow = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public async Task Outside_a_work_tree_is_none()
    {
        var info = await GitInfo.ReadAsync(_dir);

        Assert.Equal(GitInfo.None, info);
        Assert.Equal("none", info.Label);
        Assert.Equal(GitInfo.None, await GitInfo.ReadAsync(Path.Combine(_dir, "missing")));
        Assert.Equal(GitInfo.None, await GitInfo.ReadAsync(null));
    }

    [Fact]
    public async Task Credential_username_wins_then_origin_url_user()
    {
        Git("init", "-q");
        Git("config", "user.name", "Test User");
        Git("config", "user.email", "test@example.com");
        Git("remote", "add", "origin", "https://urluser:secret-token@github.com/o/r.git");
        Git("config", "credential.https://github.com.username", "credUser");

        var withCredential = await GitInfo.ReadAsync(_dir);
        Git("config", "--unset", "credential.https://github.com.username");
        var withUrl = await GitInfo.ReadAsync(_dir);

        Assert.Equal((true, "credUser", "Test User", "test@example.com"), (withCredential.InRepo, withCredential.PushAccount, withCredential.UserName, withCredential.UserEmail));
        Assert.Equal("urluser", withUrl.PushAccount); // the token part is never returned
        Assert.Equal("urluser", withUrl.Label);
    }

    [Fact]
    public async Task Repo_without_any_account_hint_still_reports_the_repo()
    {
        Git("init", "-q");
        Git("remote", "add", "origin", "git@github.com:o/r.git"); // not https: no user to take, so only gh can answer

        var info = await GitInfo.ReadAsync(_dir); // PushAccount is gh's login if gh is signed in here, else null

        Assert.True(info.InRepo);
    }

    [Fact]
    public async Task Branch_upstream_and_commits_ahead_and_behind()
    {
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test User");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false"); // a throwaway repo: a global signing setting must not need a key here
        Git("init", "-q", "--bare", "remote.git");
        Git("remote", "add", "origin", "remote.git");
        Git("commit", "-q", "--allow-empty", "-m", "one");
        var noUpstream = await GitInfo.ReadAsync(_dir);
        Git("commit", "-q", "--allow-empty", "-m", "two");
        Git("push", "-q", "-u", "origin", "main");
        Git("reset", "-q", "--hard", "HEAD~1"); // one behind
        Git("commit", "-q", "--allow-empty", "-m", "three"); // one ahead

        var info = await GitInfo.ReadAsync(_dir);
        Git("checkout", "-q", "--detach");
        var detached = await GitInfo.ReadAsync(_dir);

        Assert.Equal(("main", null, null, null), (noUpstream.Branch, noUpstream.Upstream, noUpstream.Ahead, noUpstream.Behind));
        Assert.Equal(("main", "origin/main", 1, 1), (info.Branch, info.Upstream, info.Ahead, info.Behind));
        Assert.Matches("^[0-9a-f]{7,}$", detached.Branch); // the short commit id
    }

    [Theory]
    [InlineData("2\t1", 2, 1)]
    [InlineData("0\t0\n", 0, 0)]
    [InlineData(null, null, null)]
    [InlineData("fatal", null, null)]
    public void Counts_parses_ahead_and_behind(string? output, int? ahead, int? behind) =>
        Assert.Equal((ahead, behind), GitInfo.Counts(output));
}
