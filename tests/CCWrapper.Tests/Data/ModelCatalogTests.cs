using System.IO;
using CCWrapper.Data;

namespace CCWrapper.Tests.Data;

public sealed class ModelCatalogTests : IDisposable
{
    readonly FakeHome _home = new();

    public void Dispose() => _home.Dispose();

    static string Catalog(double fetchedAt, string models) =>
        $$"""{"version":2,"fetchedAt":{{fetchedAt}},"catalog":{"surface":"cc","config":{"id":"cc","models":[""" + models + "]}}}";

    const string Main = """
        {"id":"claude-opus-9-1","name":"Opus 9.1","short_name":"Opus","section":"main","runtime":{"family":"opus"}},
        {"id":"claude-fable-9","name":"Fable 9","short_name":"Fable","section":"main","badge":{"message":"Requires usage credits"}},
        {"id":"claude-haiku-9","name":"Haiku 9","short_name":"Haiku","section":"main"},
        {"id":"claude-haiku-8","name":"Haiku 8","short_name":"Haiku","section":"main"},
        {"id":"claude-opus-9","name":"Opus 9","short_name":"Opus","section":"overflow","runtime":{"family":"opus"}},
        {"name":"no id","section":"main"}
        """;

    [Fact]
    public void Main_models_one_per_family_with_their_badge()
    {
        var a = _home.AddAccount("c");
        FakeHome.WriteFile(Path.Combine(a.ConfigRoot, "cache", "model-catalog", "org-x-cc.json"), Catalog(1000, Main));

        Assert.Equal(
        [
            new CatalogModel("claude-opus-9-1", "opus", "Opus 9.1", null),
            new CatalogModel("claude-fable-9", "fable", "Fable 9", "Requires usage credits"),
            new CatalogModel("claude-haiku-9", "haiku", "Haiku 9", null),
        ], ModelCatalog.Read(a));
    }

    [Fact]
    public void Newest_cli_catalog_wins_and_the_desktop_one_is_ignored()
    {
        var a = _home.AddAccount("c");
        var dir = Path.Combine(a.ConfigRoot, "cache", "model-catalog");
        FakeHome.WriteFile(Path.Combine(dir, "old-org-cc.json"), Catalog(1000, """{"id":"claude-opus-8","name":"Opus 8","short_name":"Opus","section":"main"}"""));
        FakeHome.WriteFile(Path.Combine(dir, "org-cc.json"), Catalog(2000, """{"id":"claude-opus-9","name":"Opus 9","short_name":"Opus","section":"main"}"""));
        FakeHome.WriteFile(Path.Combine(dir, "org-ccd.json"), Catalog(3000, """{"id":"claude-opus-10","name":"Opus 10","short_name":"Opus","section":"main"}"""));
        FakeHome.WriteFile(Path.Combine(dir, "broken-cc.json"), "{not json");

        Assert.Equal("Opus 9", Assert.Single(ModelCatalog.Read(a)).Name);
    }

    [Fact]
    public void No_catalog_is_an_empty_list() => Assert.Empty(ModelCatalog.Read(_home.AddAccount("c")));
}
