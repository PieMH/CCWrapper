using System.Windows;
using CCWrapper.Core;
using CCWrapper.Views;

namespace CCWrapper.Tests.Views;

public sealed class ThemeLoadTests
{
    /// <summary>A missing theme key or a broken style only fails when a view is built, at runtime: build every view with
    /// App.xaml's resources loaded (MainWindow brings the sidebar, panels, compose box and bottom bar). Nothing is shown.</summary>
    [Fact]
    public void Every_view_builds_with_the_theme()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App(); // the one Application of this test process
                app.InitializeComponent();
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
                Assert.NotNull(main.FindResource("Accent"));
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }
}
