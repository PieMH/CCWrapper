using System.IO;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;
using CCWrapper.Views;

namespace CCWrapper.Tests.Shell;

public sealed class WorktreeTests
{
    static readonly Account C = new("c", @"C:\h\.claude-c", @"C:\h\.claude-c\.claude.json", "c@x.com");

    [Theory]
    [InlineData(@"C:\repo\.claude\worktrees\kind-hopper", "kind-hopper", @"C:\repo\.claude\worktrees\kind-hopper")]
    [InlineData(@"C:\repo\.claude\worktrees\kind-hopper\src\app", "kind-hopper", @"C:\repo\.claude\worktrees\kind-hopper")]
    [InlineData("C:/repo/.Claude/Worktrees/x/", "x", @"C:\repo\.Claude\Worktrees\x")]
    public void WorktreeOf_finds_the_worktree_and_its_root(string cwd, string name, string root) =>
        Assert.Equal((name, root), ShellModel.WorktreeOf(cwd));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\repo")]
    [InlineData(@"C:\repo\.claude\worktrees")] // the folder itself is no worktree
    [InlineData(@"C:\repo\.claude\settings\worktrees\x")]
    public void WorktreeOf_is_null_outside_a_worktree(string? cwd) => Assert.Null(ShellModel.WorktreeOf(cwd));

    [Fact]
    public void Adopt_follows_a_session_into_its_worktree_and_stays_there_after_a_cd()
    {
        var tab = new LiveSession(C) { SessionId = "s1", Cwd = @"C:\repo", Worktree = "…", WorktreeLaunch = true };
        var inWorktree = new RunningSession(10, "s1", @"C:\repo\.claude\worktrees\w1", null, SessionStatus.Idle, null, "interactive", null);

        ShellModel.Adopt(tab, inWorktree);
        Assert.Equal(("w1", @"C:\repo\.claude\worktrees\w1", C.Paths.Transcript(@"C:\repo\.claude\worktrees\w1", "s1")), (tab.Worktree, tab.Cwd, tab.TranscriptPath));

        ShellModel.Adopt(tab, inWorktree with { Cwd = @"C:\repo\.claude\worktrees\w1\src" }); // a cd inside: still the worktree root
        Assert.Equal(@"C:\repo\.claude\worktrees\w1", tab.Cwd);

        ShellModel.Adopt(tab, inWorktree with { SessionId = "s2" }); // /clear: the new transcript is in the worktree's project folder
        Assert.Equal(C.Paths.Transcript(@"C:\repo\.claude\worktrees\w1", "s2"), tab.TranscriptPath);
    }

    [Fact]
    public void Adopt_marks_a_normal_session_in_a_worktree_but_leaves_its_folder_and_transcript()
    {
        var launch = @"C:\repo\.claude\worktrees\foo\sub";
        var tab = new LiveSession(C) { SessionId = "s1", Cwd = launch, TranscriptPath = C.Paths.Transcript(launch, "s1") };
        var reg = new RunningSession(10, "s1", launch, null, SessionStatus.Idle, null, "interactive", null);

        ShellModel.Adopt(tab, reg with { Cwd = @"C:\repo\.claude\worktrees\foo\other" }); // a cd inside
        ShellModel.Adopt(tab, reg with { SessionId = "s2" });
        Assert.Equal(("foo", launch, C.Paths.Transcript(launch, "s2")), (tab.Worktree, tab.Cwd, tab.TranscriptPath));
    }

    [Fact]
    public void ResumeOptions_use_the_current_folder_else_the_launch_folder()
    {
        var o = new LaunchOptions(@"C:\repo", Worktree: true, Name: "n", Fork: true);
        Assert.Equal(new LaunchOptions(@"C:\repo\.claude\worktrees\w1", "s1", Worktree: true), TerminalHost.ResumeOptions(o, @"C:\repo\.claude\worktrees\w1", "s1"));
        Assert.Equal(@"C:\repo", TerminalHost.ResumeOptions(o, null, "s1").Cwd);
    }

    [Fact]
    public void InGitRepo_looks_up_for_a_git_folder_or_file()
    {
        var dir = Directory.CreateTempSubdirectory("ccw-wt-").FullName;
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(dir, "a", "b")).FullName;
            Assert.False(ShellModel.InGitRepo(sub));
            File.WriteAllText(Path.Combine(dir, "a", ".git"), "gitdir: x"); // a worktree has a .git file
            Assert.True(ShellModel.InGitRepo(sub));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
