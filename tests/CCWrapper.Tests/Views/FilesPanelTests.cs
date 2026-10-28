using CCWrapper.Data;
using CCWrapper.Views;

namespace CCWrapper.Tests.Views;

public sealed class FilesPanelTests
{
    static FileChange Change(string rel, string full) => new(rel, full, ChangeKind.Modified, false, 1, 0, 10, 10, null);

    static readonly FileChange[] Sample =
    [
        Change(@"src\Views\FilesPanel.xaml", @"C:\p\src\Views\FilesPanel.xaml"),
        Change(@"src\Core\Paths.cs", @"C:\p\src\Core\Paths.cs"),
        Change(@"README.md", @"C:\p\README.md"),
        Change(@"D:\other\notes.TXT", @"D:\other\notes.TXT"),
    ];

    [Theory]
    [InlineData("", 4)]
    [InlineData("readme", 1)]      // case-insensitive file name
    [InlineData("NOTES", 1)]
    [InlineData("views", 1)]       // a folder in the relative path
    [InlineData("src", 2)]
    [InlineData("nothing", 0)]
    public void Filter_matches_name_and_path_ignoring_case(string query, int count) =>
        Assert.Equal(count, FileTree.Filter(Sample, query).Count());

    [Fact]
    public void Filtered_tree_keeps_only_the_matching_files_in_their_folders()
    {
        var tree = FileTree.Build(FileTree.Filter(Sample, "paths"));

        var nodes = FileTree.Walk(tree).Select(n => n.Name).ToList();
        Assert.Equal(["src", "Core", "Paths.cs"], nodes);
        Assert.All(FileTree.Walk(tree).Where(n => n.IsFolder), n => Assert.True(n.IsExpanded));
    }

    [Fact]
    public void Nodes_know_their_path_on_disk()
    {
        var tree = FileTree.Build(Sample);
        var byName = FileTree.Walk(tree).ToDictionary(n => n.Name, n => n.Location);

        Assert.Equal(@"C:\p\src", byName["src"]);
        Assert.Equal(@"C:\p\src\Views", byName["Views"]);
        Assert.Equal(@"C:\p\README.md", byName["README.md"]);
        Assert.Equal(@"D:\other", byName["D:\\other"]);
        Assert.Null(byName[FileTree.OutsideName]); // no folder to open
    }

    [Fact]
    public void Explorer_gets_one_quoted_token_so_commas_and_spaces_survive()
    {
        Assert.Equal("/select,\"C:\\a b\\c.cs\"", FileTree.ExplorerArgs(@"C:\a b\c.cs", false));
        Assert.Equal("/select,\"C:\\p\\a,b.txt\"", FileTree.ExplorerArgs(@"C:\p\a,b.txt", false));
        Assert.Equal("\"C:\\a b\"", FileTree.ExplorerArgs(@"C:\a b", true));
        Assert.Equal("\"C:\\p\\x,y\"", FileTree.ExplorerArgs(@"C:\p\x,y", true));
        Assert.Null(FileTree.ExplorerArgs("C:\\p\\a\"b", false));
    }
}
