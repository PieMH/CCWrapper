using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CCWrapper.Core;

namespace CCWrapper.Views;

/// <summary>Version, the update switch (applies at once, saved in app.json) and the project's documents on GitHub.</summary>
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
}
