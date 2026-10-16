using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;

namespace CCWrapper.Views;

/// <summary>"Who's working?": one card per account and the recent sessions of all of them.
/// Without <paramref name="switchingFrom"/> it is the start screen and opens a MainWindow; with it (Switch in the top bar,
/// shown as a dialog) it restarts the process as the chosen account.</summary>
public partial class AccountPicker : Window
{
    readonly Account? _current;

    public AccountPicker(Account? switchingFrom = null)
    {
        _current = switchingFrom;
        InitializeComponent();
        if (switchingFrom is not null)
        {
            Title = "Switch account";
            Heading.Text = "Switch to…";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        Loaded += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        try
        {
            var now = DateTimeOffset.Now;
            var (cards, recent) = await Task.Run(() => // reads files: off the UI thread
            {
                var accounts = AccountStore.Discover();
                return (accounts.Select(a => AccountCard.From(a, AccountReader.Read(a), SessionCatalog.Running(a).Count, now)).ToList(),
                    SessionCatalog.Recent(accounts).Select(e => RecentRow.From(e, now)).ToList());
            });
            var shown = new ObservableCollection<AccountCard>(cards);
            Cards.ItemsSource = shown;
            Recent.ItemsSource = recent;
            for (var i = 0; i < shown.Count; i++) _ = CheckUsageAsync(shown, i); // all accounts in parallel
            Status.Text = cards.Count == 0 ? @"No Claude Code accounts found (~\.claude, ~\.claude-x)" : recent.Count == 0 ? "No sessions yet" : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = ex.Message;
        }
        Status.Visibility = Status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Runs /usage hidden for the card's account (see <see cref="UsageProbe"/>) and shows the fresh numbers.</summary>
    static async Task CheckUsageAsync(ObservableCollection<AccountCard> cards, int i)
    {
        var card = cards[i];
        cards[i] = card with { AsOf = "checking usage…" };
        var fresh = await UsageProbe.RefreshAsync(card.Account);
        var info = fresh ? await Task.Run(() => AccountReader.Read(card.Account)) : null;
        cards[i] = info is null ? card : AccountCard.From(card.Account, info, 0, DateTimeOffset.Now) with { Running = card.Running };
    }

    void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountCard card }) Choose(card.Account, null);
    }

    void OnSessionClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: RecentRow row }) Choose(row.Entry.Account, row.Entry);
    }

    void Choose(Account account, SessionEntry? entry)
    {
        if (_current is null) // start screen: no window yet
        {
            var main = new MainWindow(account);
            main.Show();
            Close();
            if (entry is not null) _ = main.OpenEntry(entry);
        }
        else if (account.Letter == _current.Letter) // already this account: resume in the window that opened the picker
        {
            Close();
            if (entry is not null && Owner is MainWindow main) _ = main.OpenEntry(entry);
        }
        else if (entry is null) App.RestartAsAccount(account);
        else // same restart as App.RestartAsAccount, plus the session for MainWindow to resume
        {
            Process.Start(Environment.ProcessPath!, ["--account", account.Letter, "--resume", entry.Id]);
            Application.Current.Shutdown();
        }
    }
}
