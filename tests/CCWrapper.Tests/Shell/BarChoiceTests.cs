using CCWrapper.Data;
using CCWrapper.Views;

namespace CCWrapper.Tests.Shell;

public sealed class BarChoiceTests
{
    static readonly CatalogModel[] Catalog =
        [new("claude-opus-9-1", "opus", "Opus 9.1", null), new("claude-fable-9", "fable", "Fable 9", "Requires usage credits")];

    [Theory]
    [InlineData("opus", "Opus 9.1")] // the catalog's version, not one kept in the app
    [InlineData("Fable", "Fable 9")]
    [InlineData("claude-fable-9", "Fable 9")]
    [InlineData("Opus 5.5", "Opus 5.5")] // a display name is left alone
    [InlineData("claude-sonnet-5-5", "Sonnet 5.5")] // a full id outside the catalog gets its version from the id
    [InlineData("haiku", "haiku")]
    [InlineData("gpt-5", "gpt-5")]
    [InlineData(null, null)]
    public void Aliases_and_ids_get_the_catalog_name(string? model, string? label) => Assert.Equal(label, BarFormat.ModelLabel(model, Catalog));

    [Theory]
    [InlineData("opus", "opus")] // no catalog: no version to guess
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-fable-5-1", "Fable 5.1")]
    public void Without_a_catalog_only_full_ids_get_a_version(string model, string label) => Assert.Equal(label, BarFormat.ModelLabel(model));

    [Theory]
    [InlineData("claude-opus-5-5-20260101", "Opus 5.5")]
    [InlineData("claude-haiku-4-5-20251001", "Haiku 4.5")]
    [InlineData("claude-sonnet-5", "Sonnet 5")]
    [InlineData("claude-opus-5-5[1m]", null)]
    [InlineData("Opus 5.5", null)]
    public void Model_name_reads_any_family_from_a_full_id(string id, string? name) => Assert.Equal(name, BarFormat.ModelName(id));

    [Fact]
    public void Model_rows_are_the_catalog_else_the_bare_aliases()
    {
        Assert.Equal(new (string, string, string?)[] { ("opus", "Opus 9.1", null), ("fable", "Fable 9", "Requires usage credits") }, BarFormat.ModelRows(Catalog));
        Assert.Equal(new (string, string, string?)[] { ("opus", "Opus", null), ("sonnet", "Sonnet", null), ("haiku", "Haiku", null) }, BarFormat.ModelRows([]));
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
