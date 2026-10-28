using CCWrapper.Views;

namespace CCWrapper.Tests.Shell;

public sealed class BarChoiceTests
{
    [Theory]
    [InlineData("opus", "Opus 5.5")]
    [InlineData("Fable", "Fable 5.1")]
    [InlineData("Opus 5.5", "Opus 5.5")] // a display name is left alone
    [InlineData("claude-opus-5-5", "claude-opus-5-5")]
    [InlineData(null, null)]
    public void Bare_model_aliases_get_their_version(string? model, string? label) => Assert.Equal(label, BarFormat.ModelLabel(model));

    [Fact]
    public void Model_table_covers_the_aliases_and_only_fable_has_a_note()
    {
        Assert.Equal(["opus", "sonnet", "haiku", "fable"], BarFormat.Models.Select(m => m.Alias));
        Assert.Equal(["fable"], BarFormat.Models.Where(m => m.Note is not null).Select(m => m.Alias));
        Assert.All(BarFormat.Models, m => Assert.Contains(m.Alias, m.Label, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("  ⏵⏵ auto mode on (shift+tab to cycle) · Ultracode on", true)]
    [InlineData("  Ultracode on", true)]
    [InlineData("  ultracode off", false)]
    [InlineData("  ? for shortcuts", null)]
    [InlineData("we should turn Ultracode on later", null)] // must start the row or follow a "·"
    public void Ultracode_is_read_from_the_footer_rows(string footer, bool? on) =>
        Assert.Equal(on, BarFormat.UltracodeFromScreen("Claude Code\n\n❯ \n──────\n" + footer + "\n"));

    [Fact]
    public void Ultracode_ignores_older_rows_and_missing_screens()
    {
        Assert.Null(BarFormat.UltracodeFromScreen("  Ultracode on\n" + string.Concat(Enumerable.Repeat("some output\n", 12))));
        Assert.Null(BarFormat.UltracodeFromScreen(null));
    }
}
