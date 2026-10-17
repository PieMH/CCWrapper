using System.IO;
using CCWrapper.Data;

namespace CCWrapper.Tests.Data;

public sealed class SlashCommandsTests : IDisposable
{
    readonly FakeHome _home = new();
    readonly string _project;

    public SlashCommandsTests() => _project = Directory.CreateTempSubdirectory("ccw-proj-").FullName;

    public void Dispose()
    {
        _home.Dispose();
        Directory.Delete(_project, recursive: true);
    }

    static SlashCommand C(string name) => new(name, "", "t");

    [Fact]
    public void Filter_puts_prefix_matches_before_substring_matches_case_insensitively()
    {
        var all = new[] { C("review"), C("pr-comments"), C("compact"), C("security-review"), C("config") };
        Assert.Equal(["compact", "config", "pr-comments"], SlashCommands.Filter(all, "CO").Select(c => c.Name));
        Assert.Equal(["review", "security-review"], SlashCommands.Filter(all, "review").Select(c => c.Name));
        Assert.Equal(5, SlashCommands.Filter(all, "").Count);
        Assert.Empty(SlashCommands.Filter(all, "zzz"));
    }

    [Fact]
    public void List_reads_commands_and_skills_of_the_account_and_the_project()
    {
        var account = _home.AddAccount("x");
        var root = account.ConfigRoot;
        FakeHome.WriteFile(Path.Combine(root, "commands", "plain.md"), "\n# Do the plain thing\nbody\n");
        FakeHome.WriteFile(Path.Combine(root, "commands", "front.md"), "---\ndescription: \"With frontmatter\"\nmodel: opus\n---\nbody");
        FakeHome.WriteFile(Path.Combine(root, "commands", "nofront-desc.md"), "---\nmodel: opus\n---\n\nFirst   line\nsecond");
        FakeHome.WriteFile(Path.Combine(root, "commands", "git", "sub", "deep.md"), "Nested");
        FakeHome.WriteFile(Path.Combine(root, "skills", "pdf", "SKILL.md"), "---\nname: pdf\ndescription: Read PDFs\n---\n");
        FakeHome.WriteFile(Path.Combine(_project, ".claude", "commands", "proj.md"), "Project one");
        FakeHome.WriteFile(Path.Combine(_project, ".claude", "skills", "folder-name", "SKILL.md"), "no frontmatter");

        var list = SlashCommands.List(account, _project).ToDictionary(c => c.Name);

        Assert.Equal("Do the plain thing", list["plain"].Description);
        Assert.Equal("user", list["plain"].Source);
        Assert.Equal("With frontmatter", list["front"].Description);
        Assert.Equal("First line", list["nofront-desc"].Description);
        Assert.Equal("Nested", list["git:sub:deep"].Description);
        Assert.Equal("Read PDFs", list["pdf"].Description);
        Assert.Equal("Project one", list["proj"].Description);
        Assert.Equal("project", list["proj"].Source);
        Assert.Contains("folder-name", list.Keys);
        Assert.Contains("compact", list.Keys);
        Assert.Equal(list.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), list.Keys);
    }

    [Fact]
    public void List_tolerates_missing_folders_and_lets_project_then_user_win_over_built_ins()
    {
        var account = _home.AddAccount("x");
        var bare = SlashCommands.List(account, @"C:\does\not\exist");
        Assert.Contains(bare, c => c.Name == "compact" && c.Source == "built-in");
        Assert.Equal(bare.Count, SlashCommands.List(account, null).Count);

        FakeHome.WriteFile(Path.Combine(account.ConfigRoot, "commands", "compact.md"), "User compact");
        FakeHome.WriteFile(Path.Combine(_project, ".claude", "commands", "Compact.md"), "Project compact");
        FakeHome.WriteFile(Path.Combine(account.ConfigRoot, "commands", "init.md"), "User init");
        var list = SlashCommands.List(account, _project);
        Assert.Single(list, c => c.Name.Equals("compact", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Project compact", list.Single(c => c.Name.Equals("compact", StringComparison.OrdinalIgnoreCase)).Description);
        Assert.Equal("User init", list.Single(c => c.Name == "init").Description);
    }
}
