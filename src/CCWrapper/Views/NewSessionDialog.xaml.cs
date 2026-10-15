using System.IO;
using System.Windows;
using System.Windows.Controls;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;

namespace CCWrapper.Views;

/// <summary>Folder, model, effort, permission mode and name of a new session. A combo left on "(account default…)"
/// gives null, so claude.exe gets no flag and the account's own setting applies.</summary>
public partial class NewSessionDialog : Window
{
    const string Default = "(account default";

    /// <summary>Set when the dialog closes with Start.</summary>
    public LaunchOptions? Result { get; private set; }

    public NewSessionDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        var account = AppState.Current.Account;
        AccountInfo? info = null;
        IReadOnlyList<RecentProject> projects = [];
        try { (info, projects) = await Task.Run(() => (AccountReader.Read(account), SessionCatalog.RecentProjects(account))); } // file IO
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a file vanished mid-read: empty lists, the user can still type
        Folder.ItemsSource = projects.Select(p => p.Path).ToList();
        if (Folder.Text.Length == 0 && projects.Count > 0) Folder.SelectedIndex = 0;
        Fill(Model, ["opus", "sonnet", "haiku"], info?.Model);
        Fill(Effort, ["low", "medium", "high", "xhigh", "max"], info?.Effort);
        Fill(Mode, ["acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan"], info?.DefaultMode);
    }

    static void Fill(ComboBox box, string[] items, string? accountDefault)
    {
        box.ItemsSource = new[] { accountDefault is null ? Default + ")" : $"{Default}: {accountDefault})" }.Concat(items).ToList();
        box.SelectedIndex = 0;
    }

    static string? Pick(ComboBox box)
    {
        var text = box.IsEditable ? box.Text : box.SelectedItem as string;
        return string.IsNullOrWhiteSpace(text) || text.StartsWith(Default) ? null : text.Trim();
    }

    string FolderPath => ShellModel.NormalizeFolder(Folder.Text);

    void OnFolderChanged(object sender, TextChangedEventArgs e) => StartButton.IsEnabled = Directory.Exists(FolderPath);

    void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Project folder" };
        if (Directory.Exists(FolderPath)) dialog.InitialDirectory = FolderPath;
        if (dialog.ShowDialog(this) == true) Folder.Text = dialog.FolderName;
    }

    void OnStart(object sender, RoutedEventArgs e)
    {
        Result = new LaunchOptions(FolderPath, null, Pick(Model), Pick(Effort), Pick(Mode),
            string.IsNullOrWhiteSpace(SessionName.Text) ? null : SessionName.Text.Trim());
        DialogResult = true;
    }
}
