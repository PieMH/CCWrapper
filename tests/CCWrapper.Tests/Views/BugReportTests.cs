using CCWrapper.Core;
using CCWrapper.Views;

namespace CCWrapper.Tests.Views;

public sealed class BugReportTests
{
    const string Form = "https://github.com/PieMH/CCWrapper/issues/new?template=bug_report.yml";

    [Fact]
    public void Form_url_is_the_repo_issue_form_with_the_versions_by_field_id()
    {
        Assert.StartsWith(AppUpdate.Repo + "/issues/new?", Form);
        Assert.Equal(Form + "&app-version=0.1.0&windows=Microsoft%20Windows%2010.0.26300&claude-version=2.1.0",
            AboutWindow.BugReportUrl("0.1.0", "Microsoft Windows 10.0.26300", "2.1.0"));
    }

    [Fact]
    public void Spaces_ampersands_and_hashes_are_escaped()
    {
        Assert.Equal(Form + "&app-version=0.1.0&windows=Windows%20%26%2011%20%232&claude-version=2.1.0%20%23%26",
            AboutWindow.BugReportUrl("0.1.0", "Windows & 11 #2", "2.1.0 #&"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unknown_claude_version_leaves_its_field_out(string? claude) =>
        Assert.Equal(Form + "&app-version=0.1.0&windows=Windows%2011", AboutWindow.BugReportUrl("0.1.0", "Windows 11", claude));
}
