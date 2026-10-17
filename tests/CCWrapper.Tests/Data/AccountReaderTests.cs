using System.IO;
using CCWrapper.Data;

namespace CCWrapper.Tests.Data;

public sealed class AccountReaderTests : IDisposable
{
    readonly FakeHome _home = new();

    public void Dispose() => _home.Dispose();

    const string Global = """
        {"oauthAccount":{"emailAddress":"x@example.com","displayName":"Xy","organizationName":"Org","organizationType":"claude_team","billingType":"stripe"},
         "cachedUsageUtilization":{"fetchedAtMs":1791556083611,"utilization":{
            "five_hour":{"utilization":12,"resets_at":"2026-10-09T19:19:59.859459+00:00"},
            "seven_day":{"utilization":81.5,"resets_at":"2026-10-11T19:59:59+00:00"},"seven_day_opus":null}}}
        """;

    [Fact]
    public void Reads_oauth_usage_and_settings_defaults()
    {
        var a = _home.AddAccount("a", Global);
        FakeHome.WriteFile(a.Paths.Settings,
            """{"model":"opus","modelSettings":{"claude-sonnet-5":{"effortLevel":"low"},"claude-opus-5-5":{"effortLevel":"xhigh"}},"effortLevel":"medium","permissions":{"defaultMode":"acceptEdits"}}""");

        var info = AccountReader.Read(a);

        Assert.Equal("x@example.com", info.Email);
        Assert.Equal("Xy", info.DisplayName);
        Assert.Equal("Org", info.OrganizationName);
        Assert.Equal("claude_team", info.OrganizationType);
        Assert.Equal("stripe", info.BillingType);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791556083611), info.UsageFetchedAt);
        Assert.Equal(12, info.FiveHour!.Utilization);
        Assert.Equal(DateTimeOffset.Parse("2026-10-09T19:19:59.859459+00:00"), info.FiveHour.ResetsAt);
        Assert.Equal(81.5, info.SevenDay!.Utilization);
        Assert.Equal("opus", info.Model);
        Assert.Equal("xhigh", info.Effort); // the opus entry, not the top-level "medium"
        Assert.Equal("acceptEdits", info.DefaultMode);
    }

    [Fact]
    public void Effort_falls_back_to_top_level()
    {
        var a = _home.AddAccount("x", "{}");
        FakeHome.WriteFile(a.Paths.Settings, """{"model":"opus[1m]","modelSettings":{"claude-sonnet-5":{"effortLevel":"low"}},"effortLevel":"high"}""");

        Assert.Equal("high", AccountReader.Read(a).Effort);
    }

    [Fact]
    public void Missing_or_broken_files_give_nulls()
    {
        var a = _home.AddAccount("x", "{ broken");
        var b = AccountReader.Read(a); // no settings.json either

        Assert.Equal(new AccountInfo(null, null, null, null, null, null, null, null, null, null, null), b);
    }

    [Fact]
    public void Tolerates_a_file_rewritten_mid_read()
    {
        var a = _home.AddAccount("x", """{"oauthAccount":{"emailAdd""");
        string? email = null;
        // A thread of its own, not the thread pool: on a busy CI runner a pool task can start seconds late, and one missed even a 2 s
        // window. 50 attempts (2 s), not the default 4 (120 ms), for the same reason.
        var reader = new Thread(() =>
        {
            using var doc = JsonUtil.Load(a.GlobalConfigPath, attempts: 50);
            email = doc?.RootElement.GetProperty("oauthAccount").GetProperty("emailAddress").GetString();
        });
        reader.Start();
        Thread.Sleep(20);
        File.WriteAllText(a.GlobalConfigPath, Global);
        reader.Join();

        Assert.Equal("x@example.com", email);
    }
}
