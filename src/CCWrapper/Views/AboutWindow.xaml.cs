using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using CCWrapper.Core;
using CCWrapper.Terminal;

namespace CCWrapper.Views;

/// <summary>Version, the update switch (applies at once, saved in app.json), the project's documents on GitHub and the bug
/// report button, which only opens the prefilled issue form in the browser.</summary>
public partial class AboutWindow : Window
{
    static readonly (string Label, string File)[] Documents =
    [
        ("Privacy", "PRIVACY.md"), ("Terms", "TERMS.md"), ("Code signing policy", "CODE_SIGNING_POLICY.md"),
        ("Licence", "LICENSE"), ("Third-party notices", "THIRD-PARTY-NOTICES.txt"),
    ];

    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = "Version " + AppUpdate.Version;
        UpdateSwitch.IsChecked = AppPrefs.ReadUpdateCheck() == true;
        UpdateSwitch.Checked += OnSwitch; // after the initial state, which must not write the preference
        UpdateSwitch.Unchecked += OnSwitch;
        UpdateNote.Text = "When on, CCWrapper looks for a new version at startup; GitHub sees your IP address and the app version. " +
            "A new version downloads quietly and installs when you restart. " + (AppUpdate.IsInstalled ? "" : "This copy is not installed, so it never checks.");
        foreach (var (label, file) in Documents)
        {
            var url = $"{AppUpdate.Repo}/blob/main/{file}";
            var link = new Button { Content = label, Style = (Style)FindResource("Link"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 2, 0, 2), ToolTip = url };
            link.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Links.Children.Add(link);
        }
    }

    void OnSwitch(object sender, RoutedEventArgs e) => AppUpdate.SetEnabled(UpdateSwitch.IsChecked == true);

    /// <summary>The GitHub issue form with the versions filled in. Nothing leaves the PC until the user submits it there; the
    /// Claude Code field is left out when it is unknown.</summary>
    public static string BugReportUrl(string appVersion, string windows, string? claudeVersion)
    {
        var url = $"{AppUpdate.Repo}/issues/new?template=bug_report.yml&app-version={Uri.EscapeDataString(appVersion)}&windows={Uri.EscapeDataString(windows)}";
        if (!string.IsNullOrWhiteSpace(claudeVersion)) url += "&claude-version=" + Uri.EscapeDataString(claudeVersion);
        return url;
    }

    async void OnBugReport(object sender, RoutedEventArgs e)
    {
        BugButton.IsEnabled = false; // one browser tab per click, however fast the clicks come
        BugNote.Text = "Checking the Claude Code version…";
        BugNote.Visibility = Visibility.Visible;
        try
        {
            // The version does not depend on the account: the open window's, else the default profile (~\.claude) a bare claude would use.
            var account = AppState.Current?.Account ?? new Account("a", null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"), "");
            var claudeVersion = await ClaudeCli.VersionAsync(account);
            BugNote.Visibility = Visibility.Collapsed;
            var url = BugReportUrl(AppUpdate.Version, RuntimeInformation.OSDescription, claudeVersion);
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { BugNote.Text = "Could not open the browser. Open this address yourself: " + url; BugNote.Visibility = Visibility.Visible; }
        }
        finally { BugButton.IsEnabled = true; }
    }
}
