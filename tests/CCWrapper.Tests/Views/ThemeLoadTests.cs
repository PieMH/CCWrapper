using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CCWrapper.Core;
using CCWrapper.Views;

namespace CCWrapper.Tests.Views;

public sealed class ThemeLoadTests
{
    /// <summary>A missing theme key or a broken style only fails when a view is built, at runtime: build every view with
    /// App.xaml's resources loaded (MainWindow brings the sidebar, panels, compose box and bottom bar). Nothing is shown.
    /// The views are built under Sun, swapped in as at start, to prove the swap reaches styles and Fluent's brushes;
    /// the one Application of this process lives here, so the checks that need it do too.</summary>
    [Fact]
    public void Every_view_builds_with_a_swapped_theme()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App(); // the one Application of this test process
                app.InitializeComponent();

                var defaults = Themes.ColorsOf(Themes.Find(Themes.DefaultId)); // Theme.xaml itself
                Assert.True(defaults.Count >= 40);
                ThemeTests.AssertReadable(Themes.Find(Themes.DefaultId), defaults);
                foreach (var theme in Themes.All.Skip(1))
                    Assert.Equal(defaults.Keys.Order(), theme.Colors.Keys.Order()); // exactly Theme.xaml's color keys

                var sun = Themes.Find("sun");
                Themes.Apply(app, sun);
                Assert.Same(sun, Themes.Active);

                var main = new MainWindow(new Account("c", @"C:\none\.claude-c", @"C:\none\.claude-c\.claude.json", "t@example.com"));
                _ = new AccountPicker();
                _ = new NewSessionDialog();
                _ = new ExtensionsWindow(new Account("c", @"C:\none\.claude-c", @"C:\none\.claude-c\.claude.json", "t@example.com"), null);
                _ = new SettingsWindow(new Account("c", @"C:\none\.claude-c", @"C:\none\.claude-c\.claude.json", "t@example.com"), null);
                _ = new AboutWindow();
                _ = new HookDialog(["PreToolUse"], "PreToolUse", "Bash", "echo hi", 5);
                _ = new PluginOptionsDialog("p",
                [
                    new("region", "Region", "Where", true, false, "eu", ["eu", "us"], true),
                    new("token", "Token", null, false, true, "", null, false),
                    new("folder", "Folder", null, false, false, "C:\\", null, false),
                ]);
                _ = new TranscriptView(@"C:\none\t.jsonl");
                _ = new Dialog("message", "title", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

                Color Of(object brush) => ((SolidColorBrush)brush).Color;
                Assert.Equal(Themes.ColorOf(sun.Colors["Accent"]), Of(main.FindResource("Accent")));
                Assert.Equal(Themes.ColorOf(sun.Colors["Muted"]), Of(new TextBlock { Style = (Style)main.FindResource("Label") }.Foreground)); // a style built after the swap
                Assert.Equal(Themes.ColorOf(sun.Colors["Bg"]), Of(app.Resources["ApplicationBackgroundBrush"])); // Fluent's own
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.True(error is null, error?.ToString());
    }
}

public sealed class ThemeTests
{
    /// <summary>Text, muted text, text on the accent and on the badges, and the status and session colors stay readable
    /// (WCAG contrast: 4.5 for text, 7 in High contrast, 3 for colored marks).</summary>
    internal static void AssertReadable(Theme theme, IReadOnlyDictionary<string, string> c)
    {
        double R(string fg, string bg) => Themes.Contrast(Themes.ColorOf(c[fg]), Themes.ColorOf(c[bg]));
        void AtLeast(double min, string fg, string bg) => Assert.True(R(fg, bg) >= min, $"{theme.Name}: {fg} on {bg} is {R(fg, bg):0.00}, below {min}");
        var text = theme.Id == "contrast" ? 7 : 4.5;
        foreach (var bg in new[] { "Bg", "Panel", "Raised", "TerminalBg" }) AtLeast(text, "Text", bg);
        foreach (var bg in new[] { "Bg", "Panel" }) AtLeast(4.5, "Muted", bg);
        AtLeast(4.5, "TerminalText", "TerminalBg");
        AtLeast(4.5, "OnAccent", "AccentFill");
        AtLeast(4.5, "GoodText", "GoodFill");
        AtLeast(4.5, "Warn", "WarnFill");
        AtLeast(3, "Accent", "Bg");
        foreach (var mark in c.Keys.Where(k => k.StartsWith("Session")).Append("Good").Append("Warn").Append("Bad")) AtLeast(3, mark, "Panel");
    }

    public static TheoryData<string> Swapped => new(Themes.All.Skip(1).Select(t => t.Id));

    [Theory]
    [MemberData(nameof(Swapped))]
    public void Theme_is_readable(string id) => AssertReadable(Themes.Find(id), Themes.Find(id).Colors);

    [Fact]
    public void Five_themes_with_distinct_ids_and_an_unknown_id_falls_back_to_default()
    {
        Assert.Equal(["default", "dark", "contrast", "space", "sun"], Themes.All.Select(t => t.Id));
        Assert.Equal(Themes.DefaultId, Themes.Find("nope").Id);
        Assert.Equal(Themes.DefaultId, Themes.Find(null).Id);
        Assert.True(Themes.Find("sun").Light);
    }

    [Theory]
    [InlineData("#000000", "#D6D6D6", false, "#000000", "#D6D6D6")] // a dark theme keeps its terminal colors
    [InlineData("#FBFAF7", "#1F2328", false, "#101215", "#D3D9E1")] // Sun under Claude Code's dark theme: a dark terminal
    [InlineData("#000000", "#D6D6D6", true, "#FFFFFF", "#1F2328")]  // Claude Code's light theme: a light terminal
    public void Terminal_follows_claude_codes_theme(string bg, string text, bool claudeLight, string wantBg, string wantText) =>
        Assert.Equal((wantBg, wantText), Themes.TerminalColors(bg, text, claudeLight));

    [Fact]
    public void Theme_choice_round_trips_and_keeps_other_keys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccw-prefs-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Null(AppPrefs.ReadTheme(path));
            Assert.Null(AppPrefs.WriteBrowserTool(false, path));
            Assert.Null(AppPrefs.WriteTheme("space", path));
            Assert.Equal("space", AppPrefs.ReadTheme(path));
            Assert.False(AppPrefs.ReadBrowserTool(path));
        }
        finally { File.Delete(path); }
    }
}
