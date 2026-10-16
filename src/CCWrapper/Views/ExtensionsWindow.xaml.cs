using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;

namespace CCWrapper.Views;

/// <summary>Plugins, marketplaces, skills, MCP servers and claude.ai connectors of one account. Every change runs the claude
/// CLI hidden (<see cref="ClaudeCli"/>): nothing is typed into an open session, except /reload-plugins when the user asks.</summary>
public partial class ExtensionsWindow : Window
{
    static readonly TimeSpan Quick = TimeSpan.FromSeconds(60), Long = TimeSpan.FromMinutes(5), Health = TimeSpan.FromMinutes(2);
    const int CardsShown = 60;
    const string ConnectorsUrl = "https://claude.ai/settings/connectors";
    static readonly string[] Tints = ["SessionBlue", "SessionPurple", "SessionGreen", "SessionCyan", "SessionOrange", "SessionPink", "SessionYellow", "SessionRed"];

    readonly Account _account;
    readonly SemaphoreSlim _writes = new(1, 1); // claude rewrites the settings files on each change: one at a time
    readonly bool _ready;
    readonly Dictionary<string, (PluginDetails Details, List<PluginOption> Options)> _details = [];
    string? _cwd;
    bool _filling;
    List<PluginInfo> _plugins = [];
    List<CatalogEntry>? _catalog;
    List<MarketplaceInfo> _markets = [];
    List<McpRow> _own = [];
    List<McpServer>? _health; // the last claude mcp list; null before the first
    Task? _healthRun;
    string? _shownPlugin;
    CancellationTokenSource? _signIn;

    public ExtensionsWindow(Account account, string? cwd)
    {
        InitializeComponent();
        _account = account;
        _cwd = cwd;
        var letter = ShellModel.LetterOf(account.Letter);
        Title = $"Plugins & connectors · {letter}";
        AccountLetter.Text = letter;
        AccountName.Text = $"Account {letter}";
        AccountLabel.Text = account.Label;
        Promise.Text = $"Runs the claude CLI for account {letter}. Open sessions are never typed into.";
        _ready = true; // the XAML's IsChecked="True" raises Checked before the fields above exist
        OnAddChanged(this, new RoutedEventArgs());
        ShowPage();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _signIn?.Cancel();
    }

    async Task LoadAsync()
    {
        var projects = await Task.Run(() =>
        {
            try { return SessionCatalog.RecentProjects(_account).Select(p => p.Path).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        });
        if (_cwd is not null && !projects.Contains(_cwd, StringComparer.OrdinalIgnoreCase)) projects.Insert(0, _cwd);
        _filling = true;
        ProjectBox.ItemsSource = projects.Select(p => new ProjectChoice(p)).ToList();
        ProjectBox.SelectedIndex = _cwd is null ? (projects.Count > 0 ? 0 : -1) : projects.FindIndex(p => p.Equals(_cwd, StringComparison.OrdinalIgnoreCase));
        _cwd = (ProjectBox.SelectedItem as ProjectChoice)?.Path;
        _filling = false;
        await Task.WhenAll(RefreshInstalledAsync(), RefreshMarketsAsync());
    }

    // ---- running claude ----

    /// <summary>Runs claude and, when <paramref name="show"/>, puts the command and how it ended in the status strip.</summary>
    async Task<CliResult> Cli(IReadOnlyList<string> args, TimeSpan timeout, bool show = true, string? stdin = null,
        CancellationToken cancel = default, string? runningText = null)
    {
        var display = ClaudeCli.Display(args);
        if (show) SetLast(runningText ?? $"Running {display}…", "TextSoft");
        CliResult r;
        try { r = await ClaudeCli.RunAsync(_account, _cwd, args, timeout, stdin, cancel); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            r = new CliResult(-1, "", "Could not run claude: " + ex.Message);
        }
        if (show) SetLast(r.Ok ? $"✓ {display}" : $"✗ {display}: {Why(r)}", r.Ok ? "TextSoft" : "Bad");
        return r;
    }

    /// <summary>A write: one at a time, shown in the status strip.</summary>
    async Task<CliResult> Write(IReadOnlyList<string> args, TimeSpan timeout, string? stdin = null)
    {
        await _writes.WaitAsync();
        try { return await Cli(args, timeout, stdin: stdin); }
        finally { _writes.Release(); }
    }

    /// <summary>An install or update. When the plugin installs by running a command its marketplace declares, claude refuses
    /// without a TTY and reports the command: show it, and run again with --accept-command only after a yes.</summary>
    async Task<CliResult> WriteWithConsent(List<string> args, string name)
    {
        var r = await Write(args, Long);
        var outcome = PluginData.ParseOutcome(r.Output);
        if (r.Ok || outcome.Command is null || outcome.Sha256 is null) return r;
        var answer = Dialog.Show(this, $"{name} installs by running this command from its marketplace:\n\n{outcome.Command}\n\nRun it?",
            "Run the marketplace's command?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes ? await Write([.. args, "--accept-command", outcome.Sha256], Long) : r;
    }

    static string Why(CliResult r) => PluginData.ParseOutcome(r.Output).Message ?? r.Line;

    void SetLast(string text, string brush)
    {
        LastCommand.Text = text;
        LastCommand.ToolTip = text;
        LastCommand.Foreground = (Brush)FindResource(brush);
    }

    static T? Json<T>(CliResult r, Func<JsonElement, T> parse) where T : class
    {
        if (!r.Ok) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.Output);
            return parse(doc.RootElement);
        }
        catch (JsonException) { return null; } // not the JSON this version printed before: show the error line instead
    }

    // ---- pages ----

    string Page => NavDiscover.IsChecked == true ? "discover" : NavMarketplaces.IsChecked == true ? "marketplaces"
        : NavSkills.IsChecked == true ? "skills" : NavServers.IsChecked == true ? "servers" : NavConnectors.IsChecked == true ? "connectors" : "installed";

    void OnNav(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ShowPage();
        if (Page is "servers" or "connectors") _ = EnsureHealthAsync();
        if (Page == "discover") _ = ShowCatalogAsync();
    }

    void ShowPage()
    {
        var page = Page;
        static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        InstalledPage.Visibility = V(page == "installed");
        DiscoverPage.Visibility = V(page == "discover");
        MarketplacesPage.Visibility = V(page == "marketplaces");
        SkillsPage.Visibility = V(page == "skills");
        ServersPage.Visibility = V(page == "servers");
        ConnectorsPage.Visibility = V(page == "connectors");
        InstalledActions.Visibility = V(page == "installed");
        ServersActions.Visibility = V(page == "servers");
        ConnectorsActions.Visibility = V(page == "connectors");
        SetHeader();
    }

    void SetHeader()
    {
        var local = _plugins.Count(p => !p.Synced);
        var connectors = _health?.Where(h => h.IsConnector).ToList();
        (PageTitle.Text, PageSubtitle.Text) = Page switch
        {
            "discover" => ("Discover", _catalog is null ? "Plugins the marketplaces offer" : $"{_catalog.Count:N0} plugins in {_catalog.Select(c => c.Marketplace).Distinct().Count()} marketplaces"),
            "marketplaces" => ("Marketplaces", "Where plugins come from. Updating one refreshes its catalog."),
            "skills" => ("Skills", "Your own skills and those of enabled plugins on this computer."),
            "servers" => ("MCP servers", $"What sessions of account {ShellModel.LetterOf(_account.Letter)} start. Health comes from claude mcp list."),
            "connectors" => ("claude.ai connectors", connectors is null ? "From your claude.ai account"
                : $"{connectors.Count} from your claude.ai account · {connectors.Count(c => c.State == McpState.Connected)} connected · {connectors.Count(c => c.State == McpState.NeedsAuth)} need sign-in"),
            _ => ("Installed plugins", $"{_plugins.Count} plugins: {local} on this computer, {_plugins.Count - local} synced from claude.ai"),
        };
    }

    async void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _filling) return;
        _cwd = (ProjectBox.SelectedItem as ProjectChoice)?.Path;
        _health = null;
        await RefreshInstalledAsync();
        if (Page is "servers" or "connectors") await EnsureHealthAsync();
    }

    // ---- installed ----

    async Task RefreshInstalledAsync()
    {
        InstalledStatus.Text = _plugins.Count == 0 ? "Reading plugins…" : "";
        var r = await Cli(["plugin", "list", "--json"], Quick, show: false);
        if (Json(r, PluginData.ParseInstalled) is not { } plugins)
        {
            InstalledStatus.Text = "Could not list the plugins: " + Why(r);
            return;
        }
        _plugins = plugins;
        InstalledStatus.Text = "";
        ShowInstalled();
        SetHeader();
        await LoadSkillsAsync();
    }

    void OnInstalledFilter(object sender, RoutedEventArgs e)
    {
        if (_ready) ShowInstalled();
    }

    void ShowInstalled()
    {
        var q = InstalledFilter.Text.Trim();
        bool Match(PluginInfo p) => q.Length == 0 || p.Id.Contains(q, StringComparison.OrdinalIgnoreCase);
        var local = _plugins.Where(p => !p.Synced).ToList();
        var synced = _plugins.Where(p => p.Synced).ToList();
        ShowAll.Content = $"All {_plugins.Count}";
        ShowLocal.Content = $"This computer {local.Count}";
        ShowSynced.Content = $"claude.ai {synced.Count}";
        InstalledCount.Text = _plugins.Count.ToString();

        var showLocal = ShowSynced.IsChecked != true;
        var showSynced = ShowLocal.IsChecked != true;
        var keep = _shownPlugin;
        var rows = showLocal ? local.Where(Match).Select(p => new PluginRowView(p, Tint(p.Name))).ToList() : [];
        LocalList.ItemsSource = rows;
        LocalHeader.Visibility = LocalList.Visibility = showLocal ? Visibility.Visible : Visibility.Collapsed;
        SyncedList.ItemsSource = showSynced ? synced.Where(Match).ToList() : [];
        SyncedHeader.Visibility = SyncedList.Visibility = showSynced && synced.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LocalList.SelectedItem = rows.FirstOrDefault(r => r.Info.Id == keep) ?? rows.FirstOrDefault();
        if (LocalList.SelectedItem is null) Details.Visibility = Visibility.Hidden;
    }

    Brush Tint(string name) => (Brush)FindResource(Tints[name.Sum(c => c) % Tints.Length]);

    async void OnPluginSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LocalList.SelectedItem is PluginRowView row) await ShowDetailsAsync(row.Info);
    }

    async Task ShowDetailsAsync(PluginInfo p)
    {
        _shownPlugin = p.Id;
        Details.Visibility = Visibility.Visible;
        DetailMono.Text = PluginRowView.MonoOf(p.Name);
        DetailMono.Foreground = Tint(p.Name);
        DetailName.Text = p.Name;
        DetailSub.Text = p.Main.Version is { } v ? $"{p.Marketplace} · v{v}" : p.Marketplace;
        DetailState.Text = p.Enabled ? "Enabled" : "Disabled";
        DetailState.Foreground = (Brush)FindResource(p.Enabled ? "Good" : "Muted");
        DetailScopes.Text = string.Join(", ", p.Installs.Select(i => i.ProjectPath is null ? i.Scope : $"{i.Scope} ({i.ProjectPath})"));
        DetailInstalled.Text = p.InstalledAt?.LocalDateTime.ToString("d MMM yyyy") ?? "—";
        DetailUpdated.Text = p.Updated?.LocalDateTime.ToString("d MMM yyyy") ?? "—";
        DetailNote.Text = string.Join("\n", p.Notes);
        DetailNoteBox.Visibility = p.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var key = p.Id + "@" + p.Main.Version;
        if (!_details.TryGetValue(key, out var d))
        {
            DetailDescription.Text = "Reading details…";
            DetailCost.Text = "—";
            DetailComponents.ItemsSource = null;
            DetailSkills.Text = "";
            ConfigureButton.IsEnabled = false;
            var details = Cli(["plugin", "details", p.Id], Quick, show: false);
            var options = Cli(["plugin", "configure", p.Id, "--json"], Quick, show: false);
            await Task.WhenAll(details, options);
            d = (PluginData.ParseDetails(details.Result.Ok ? details.Result.Output : ""), Json(options.Result, PluginData.ParseOptions) ?? []);
            _details[key] = d;
            if (_shownPlugin != p.Id) return; // another plugin was picked meanwhile
        }
        DetailDescription.Text = d.Details.Description ?? "—";
        DetailCost.Text = d.Details.AlwaysOn ?? "—";
        DetailComponents.ItemsSource = d.Details.Components;
        DetailSkills.Text = d.Details.Components.FirstOrDefault(c => c.Kind == "Skills" && c.Count > 0) is { } skills ? "Skills: " + skills.Names : "";
        ConfigureButton.IsEnabled = d.Options.Count > 0;
        ConfigureButton.ToolTip = d.Options.Count > 0 ? null : "This plugin has no options to set";
    }

    PluginInfo? Shown => _plugins.FirstOrDefault(p => p.Id == _shownPlugin);

    async void OnTogglePlugin(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginRowView row) return;
        ((UIElement)sender).IsEnabled = false; // until the refresh replaces the row: a second click would repeat the command
        var p = row.Info;
        var r = await Write(["plugin", p.Enabled ? "disable" : "enable", p.Id, "--scope", p.Main.Scope, "--json"], Quick);
        if (r.Ok) PluginsChanged();
        await RefreshInstalledAsync(); // a refused change puts the switch back
    }

    async void OnUpdatePlugin(object sender, RoutedEventArgs e)
    {
        if (Shown is not { } p) return;
        UpdateButton.IsEnabled = false;
        var r = await WriteWithConsent(["plugin", "update", p.Id, "--scope", p.Main.Scope, "--json"], p.Name);
        UpdateButton.IsEnabled = true;
        if (r.Ok) PluginsChanged();
        await RefreshInstalledAsync();
    }

    async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (Shown is not { } p) return;
        if (Dialog.Show(this, $"Uninstall {p.Name} from the {p.Main.Scope} scope?\n\nNew sessions start without its skills, agents, hooks and MCP servers.",
                "Uninstall plugin", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var r = await Write(["plugin", "uninstall", p.Id, "--scope", p.Main.Scope, "--json"], Long);
        if (r.Ok) PluginsChanged();
        await RefreshInstalledAsync();
    }

    async void OnConfigure(object sender, RoutedEventArgs e)
    {
        if (Shown is not { } p || !_details.TryGetValue(p.Id + "@" + p.Main.Version, out var d) || d.Options.Count == 0) return;
        var dialog = new PluginOptionsDialog(p.Name, d.Options) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Changes is not { Count: > 0 } changes) return;
        var r = await Write(["plugin", "configure", p.Id, "--values-stdin", "--json"], Quick, JsonSerializer.Serialize(changes));
        _details.Remove(p.Id + "@" + p.Main.Version);
        if (r.Ok) PluginsChanged();
        await ShowDetailsAsync(p);
    }

    async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        var r = await Write(["plugin", "marketplace", "update"], Long);
        button.IsEnabled = true;
        _catalog = null;
        if (r.Ok) SetLast("✓ Marketplaces updated. Update a plugin to get its latest version.", "TextSoft");
        await Task.WhenAll(RefreshInstalledAsync(), RefreshMarketsAsync());
    }

    // ---- reload banner ----

    void PluginsChanged()
    {
        var open = AppState.Current?.OpenSessions.Count(s => s.Status != SessionStatus.Exited) ?? 0;
        BannerText.Text = open == 0 ? "New sessions get this change."
            : $"New sessions get this change. {open} open session{(open == 1 ? "" : "s")} keep{(open == 1 ? "s" : "")} the old setup until reloaded.";
        ReloadButton.Visibility = open > 0 ? Visibility.Visible : Visibility.Collapsed;
        Banner.Visibility = Visibility.Visible;
    }

    void OnDismissBanner(object sender, RoutedEventArgs e) => Banner.Visibility = Visibility.Collapsed;

    // Only idle sessions: a busy one would queue the command as a prompt, a waiting one has a dialog open (TuiInput refuses).
    async void OnReloadSessions(object sender, RoutedEventArgs e)
    {
        Banner.Visibility = Visibility.Collapsed;
        int sent = 0, skipped = 0;
        foreach (var s in AppState.Current?.OpenSessions.ToList() ?? [])
        {
            if (s.Status == SessionStatus.Exited) continue;
            if (s.Status == SessionStatus.Idle && await TuiInput.SubmitAsync(s, "/reload-plugins") is null) sent++;
            else skipped++;
        }
        SetLast($"Sent /reload-plugins to {sent} session{(sent == 1 ? "" : "s")}"
            + (skipped > 0 ? $"; {skipped} busy or waiting, left alone (Claude Code shows them its own reload notice)" : ""), "TextSoft");
    }

    // ---- discover ----

    async Task ShowCatalogAsync()
    {
        if (_catalog is null)
        {
            CatalogStatus.Text = "Reading the marketplaces' catalogs…";
            var r = await Cli(["plugin", "list", "--available", "--json"], Long, show: false);
            if (Json(r, PluginData.ParseCatalog) is not { } catalog)
            {
                CatalogStatus.Text = "Could not read the catalogs: " + Why(r);
                return;
            }
            _catalog = catalog;
            CatalogHint.Text = $"Search {catalog.Count:N0} plugins";
            CatalogMarket.ItemsSource = new[] { new ComboBoxItem { Content = "All marketplaces" } }
                .Concat(catalog.GroupBy(c => c.Marketplace).OrderByDescending(g => g.Count())
                    .Select(g => new ComboBoxItem { Content = $"{g.Key} ({g.Count():N0})", Tag = g.Key }))
                .ToList();
            CatalogMarket.SelectedIndex = 0;
            SetHeader();
            ShowMarkets();
        }
        FilterCatalog();
    }

    void OnCatalogFilter(object sender, RoutedEventArgs e)
    {
        if (_ready && _catalog is not null) FilterCatalog();
    }

    // shortcut: at most CardsShown cards (a wrap panel does not virtualize); add paging if people browse past the top results.
    void FilterCatalog()
    {
        var (shown, matches) = PluginData.Search(_catalog ?? [], CatalogSearch.Text, (CatalogMarket.SelectedItem as ComboBoxItem)?.Tag as string, CardsShown);
        var installed = _plugins.Select(p => p.Id).ToHashSet();
        Cards.ItemsSource = shown.Select(c => new CardView(c, installed.Contains(c.Id))).ToList();
        CatalogStatus.Text = matches == 0 ? "No plugin matches."
            : matches > shown.Count ? $"Showing the {shown.Count} most installed of {matches:N0} · refine the search to see others"
            : $"{matches:N0} plugin{(matches == 1 ? "" : "s")}";
    }

    async void OnInstall(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CardView card) return;
        ((Button)sender).IsEnabled = false;
        var scope = (InstallScope.SelectedItem as ComboBoxItem)?.Tag as string ?? "user";
        var r = await WriteWithConsent(["plugin", "install", card.Entry.Id, "--scope", scope, "--json"], card.Entry.Name);
        if (r.Ok) PluginsChanged();
        await RefreshInstalledAsync();
        FilterCatalog();
    }

    // ---- marketplaces ----

    async Task RefreshMarketsAsync()
    {
        var r = await Cli(["plugin", "marketplace", "list", "--json"], Quick, show: false);
        if (Json(r, PluginData.ParseMarketplaces) is not { } markets)
        {
            MarketsStatus.Text = "Could not list the marketplaces: " + Why(r);
            return;
        }
        _markets = markets;
        MarketsStatus.Text = "";
        ShowMarkets();
    }

    void ShowMarkets()
    {
        MarketplacesCount.Text = _markets.Count.ToString();
        Markets.ItemsSource = _markets.Select(m => new MarketRowView(m, _catalog?.Count(c => c.Marketplace == m.Name))).ToList();
    }

    async void OnAddMarketplace(object sender, RoutedEventArgs e)
    {
        var source = MarketSource.Text.Trim();
        if (source.Length == 0) return;
        var r = await Write(["plugin", "marketplace", "add", source, "--scope", (MarketScope.SelectedItem as ComboBoxItem)?.Tag as string ?? "user", "--json"], Long);
        if (r.Ok) MarketSource.Clear();
        _catalog = null;
        await RefreshMarketsAsync();
    }

    async void OnUpdateMarketplace(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MarketRowView row) return;
        await Write(["plugin", "marketplace", "update", row.Info.Name, "--json"], Long);
        _catalog = null;
        await RefreshMarketsAsync();
    }

    async void OnRemoveMarketplace(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MarketRowView row) return;
        if (Dialog.Show(this, $"Remove the marketplace {row.Info.Name}?\n\nPlugins installed from it stay installed, without updates.",
                "Remove marketplace", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Write(["plugin", "marketplace", "remove", row.Info.Name, "--json"], Quick);
        _catalog = null;
        await RefreshMarketsAsync();
    }

    // ---- skills ----

    async Task LoadSkillsAsync()
    {
        var plugins = _plugins.Where(p => p.Enabled && p.Main.InstallPath is not null).ToList();
        var userSkills = _account.Paths.Skills;
        var rows = await Task.Run(() => PluginData.ReadSkills(userSkills, "user")
            .Concat(plugins.SelectMany(p => PluginData.ReadSkills(Path.Combine(p.Main.InstallPath!, "skills"), p.Name)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList());
        Skills.ItemsSource = rows;
        SkillsCount.Text = rows.Count.ToString();
    }

    // ---- MCP servers and connectors ----

    Task EnsureHealthAsync() => _health is not null ? Task.CompletedTask : _healthRun ??= CheckHealthAsync();

    async void OnCheckHealth(object sender, RoutedEventArgs e) => await (_healthRun ??= CheckHealthAsync());

    async Task CheckHealthAsync()
    {
        try
        {
            CheckHealthButton.IsEnabled = false;
            ServersStatus.Text = ConnectorsStatus.Text = "Checking: claude mcp list connects to every server, which can take a minute…";
            var cwd = _cwd;
            _own = await Task.Run(() => McpData.OwnServers(_account, cwd));
            ShowServers();
            var r = await Cli(["mcp", "list"], Health, runningText: "Running claude mcp list (up to 2 minutes)…");
            if (!r.Ok)
            {
                ServersStatus.Text = ConnectorsStatus.Text = "claude mcp list failed: " + r.Line;
                return;
            }
            _health = McpData.ParseList(r.Output);
            CheckedAt.Text = $"Checked at {DateTime.Now:HH:mm}";
            ServersStatus.Text = ConnectorsStatus.Text = "";
            ShowServers();
        }
        finally
        {
            CheckHealthButton.IsEnabled = true;
            _healthRun = null;
        }
    }

    void OnServerFilter(object sender, RoutedEventArgs e)
    {
        if (_ready) ShowServers();
    }

    void ShowServers()
    {
        var health = _health ?? [];
        ServerRowView Row(McpServer s, string origin, string? removeScope) => new(s, origin, removeScope, StateBrush(s.State));

        // Yours: the config files' servers with their health, plus any other non-plugin, non-connector server claude listed.
        var own = _own.Select(o => Row(health.FirstOrDefault(h => h.Name.Trim() == o.Name)
                ?? new McpServer(o.Name, o.Detail, McpState.Unknown, _health is null ? "Not checked yet" : "Not listed", null), o.Source, o.Source))
            .Concat(health.Where(h => h.Plugin is null && !h.IsConnector && !_own.Any(o => o.Name == h.Name.Trim())).Select(h => Row(h, "", null)))
            .ToList();
        OwnServers.ItemsSource = own;
        OwnEmpty.Visibility = own.Count == 0 && AddForm.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

        var fromPlugins = health.Where(h => h.Plugin is not null).ToList();
        int Count(McpState s) => fromPlugins.Count(h => h.State == s);
        StateAll.Content = $"All {fromPlugins.Count}";
        StateConnected.Content = $"{Count(McpState.Connected)} connected";
        StateAuth.Content = $"{Count(McpState.NeedsAuth)} need sign-in";
        StateNotSetUp.Content = $"{Count(McpState.NotSetUp)} not set up";
        StateFailed.Content = $"{Count(McpState.Failed)} failed";
        McpState? filter = StateConnected.IsChecked == true ? McpState.Connected : StateAuth.IsChecked == true ? McpState.NeedsAuth
            : StateNotSetUp.IsChecked == true ? McpState.NotSetUp : StateFailed.IsChecked == true ? McpState.Failed : null;
        PluginServersHeader.Text = $"From plugins · {fromPlugins.Count}";
        PluginServers.ItemsSource = fromPlugins.Where(h => filter is null || h.State == filter).Select(h => Row(h, "from " + h.Plugin, null)).ToList();

        var connectors = health.Where(h => h.IsConnector).Select(h => Row(h, "", null)).ToList();
        Connectors.ItemsSource = connectors;
        if (_health is not null)
        {
            ServersCount.Text = (own.Count + fromPlugins.Count).ToString();
            ConnectorsCount.Text = connectors.Count.ToString();
        }
        SetHeader();
    }

    Brush StateBrush(McpState state) => (Brush)FindResource(state switch
    {
        McpState.Connected => "Good",
        McpState.NeedsAuth => "Warn",
        McpState.Failed => "Bad",
        McpState.Pending => "Accent",
        _ => "Muted",
    });

    async void OnServerAction(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServerRowView row) return;
        if (row.Server.State == McpState.NeedsAuth) await SignInAsync(row.Server);
        else await RecheckAsync(row.Server);
    }

    async Task SignInAsync(McpServer server)
    {
        using var cancel = new CancellationTokenSource();
        _signIn = cancel;
        CancelRun.Visibility = Visibility.Visible;
        try
        {
            var r = await Cli(["mcp", "login", server.Name], Long, cancel: cancel.Token,
                runningText: $"Sign in to {server.Display} in the browser window that opened…");
            if (r.Ok) await RecheckAsync(server);
        }
        catch (OperationCanceledException) { SetLast("Sign-in cancelled", "TextSoft"); }
        finally
        {
            _signIn = null;
            CancelRun.Visibility = Visibility.Collapsed;
        }
    }

    void OnCancelRun(object sender, RoutedEventArgs e) => _signIn?.Cancel();

    /// <summary><c>claude mcp get</c> of one server: its fresh status replaces the one from the last list.</summary>
    async Task RecheckAsync(McpServer server)
    {
        var r = await Cli(["mcp", "get", server.Name], Quick);
        if (_health is null || McpData.StatusOf(r.Output) is not { } fresh) return;
        var i = _health.FindIndex(h => h.Name == server.Name);
        var updated = server with { State = fresh.State, Status = fresh.Status, Error = fresh.Error };
        if (i >= 0) _health[i] = updated; else _health.Add(updated);
        ShowServers();
    }

    async void OnRemoveServer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServerRowView { RemoveScope: { } scope } row) return;
        if (Dialog.Show(this, $"Remove the MCP server {row.Display} from the {scope} scope?\n\nNew sessions start without it.",
                "Remove MCP server", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var r = await Write(["mcp", "remove", row.Server.Name, "--scope", scope], Quick);
        if (!r.Ok) return;
        _health?.RemoveAll(h => h.Name == row.Server.Name);
        var cwd = _cwd;
        _own = await Task.Run(() => McpData.OwnServers(_account, cwd));
        ShowServers();
    }

    void OnOpenAdd(object sender, RoutedEventArgs e)
    {
        NavServers.IsChecked = true;
        AddForm.Visibility = Visibility.Visible;
        OwnEmpty.Visibility = Visibility.Collapsed;
        AddName.Focus();
    }

    void OnCloseAdd(object sender, RoutedEventArgs e)
    {
        AddForm.Visibility = Visibility.Collapsed;
        ShowServers();
    }

    (List<string>? Args, string? Error) AddArgs()
    {
        var transport = TransportStdio.IsChecked == true ? "stdio" : TransportSse.IsChecked == true ? "sse" : "http";
        var scope = AddProject.IsChecked == true ? "project" : AddUser.IsChecked == true ? "user" : "local";
        return McpData.AddArgs(AddName.Text, transport, AddTarget.Text, AddPairs.Text, scope);
    }

    void OnAddChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var stdio = TransportStdio.IsChecked == true;
        AddTargetLabel.Text = stdio ? "Command and arguments" : "URL";
        AddPairsLabel.Text = stdio ? "Environment variables, one KEY=value per line" : "Headers, one Name: value per line";
        var (args, error) = AddArgs();
        AddPreview.Text = args is null ? "claude mcp add …" : ClaudeCli.Display(args);
        AddError.Text = AddName.Text.Length == 0 && AddTarget.Text.Length == 0 ? "" : error ?? "";
        AddConfirm.IsEnabled = args is not null;
    }

    async void OnConfirmAdd(object sender, RoutedEventArgs e)
    {
        if (AddArgs() is not { Args: { } args }) return;
        AddConfirm.IsEnabled = false;
        var r = await Write(args, Quick);
        AddConfirm.IsEnabled = true;
        if (!r.Ok) { AddError.Text = Why(r); return; }
        var name = AddName.Text.Trim();
        AddName.Clear();
        AddTarget.Clear();
        AddPairs.Clear();
        AddForm.Visibility = Visibility.Collapsed;
        var cwd = _cwd;
        _own = await Task.Run(() => McpData.OwnServers(_account, cwd));
        ShowServers();
        if (_health is not null) await RecheckAsync(new McpServer(name, "", McpState.Unknown, "", null));
        SetLast($"✓ Added {name}. New sessions start with it.", "TextSoft");
    }

    void OnManageConnectors(object sender, RoutedEventArgs e)
    {
        if (AppState.Current is not { } app) return;
        app.Commands.OpenInBrowser(ConnectorsUrl);
        Owner?.Activate();
    }
}

/// <summary>A folder of the project picker: shows its name, holds its path.</summary>
public sealed record ProjectChoice(string Path)
{
    public override string ToString() => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } name ? name : Path;
}

public sealed class PluginRowView(PluginInfo info, Brush tint)
{
    public PluginInfo Info { get; } = info;
    public Brush Tint { get; } = tint;
    public string Name => Info.Name;
    public string Mono => MonoOf(Info.Name);
    public bool Enabled => Info.Enabled;
    public string SwitchTip => (Info.Enabled ? "Disable " : "Enable ") + Info.Name;
    public string Detail => string.Join(" · ", new[] { Info.Marketplace, Info.Main.Version is { } v ? "v" + v : null, Info.Scopes }.Where(s => !string.IsNullOrEmpty(s)));
    public string Badge => Info.Notes.Count == 1 ? "1 note" : $"{Info.Notes.Count} notes";
    public Visibility BadgeVisibility => Info.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static string MonoOf(string name) => name.Length == 0 ? "?" : char.ToUpperInvariant(name[0]).ToString();
}

public sealed class CardView(CatalogEntry entry, bool installed)
{
    public CatalogEntry Entry { get; } = entry;
    public string Mono => PluginRowView.MonoOf(Entry.Name);
    public string InstallsText => Entry.Installs == 1 ? "1 install" : $"{Entry.Installs:N0} installs";
    public Visibility InstallVisibility => installed ? Visibility.Collapsed : Visibility.Visible;
    public Visibility InstalledVisibility => installed ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class MarketRowView(MarketplaceInfo info, int? plugins)
{
    public MarketplaceInfo Info { get; } = info;
    public string CountText => plugins is { } n ? $"{n:N0} plugins" : "";
}

/// <param name="removeScope">The scope <c>claude mcp remove</c> takes for the user's own server; null for plugin servers and connectors.</param>
public sealed class ServerRowView(McpServer server, string origin, string? removeScope, Brush statusBrush)
{
    public McpServer Server { get; } = server;
    public string? RemoveScope { get; } = removeScope;
    public string Origin { get; } = origin;
    public Brush StatusBrush { get; } = statusBrush;
    public string Display => Server.Display;
    public string Mono => PluginRowView.MonoOf(Server.Display);
    public string Endpoint => Server.Endpoint.Length > 0 ? Server.Endpoint : "no URL set";
    public string? Error => Server.Error;
    public Visibility ErrorVisibility => Server.Error is null ? Visibility.Collapsed : Visibility.Visible;
    public string StatusText => Server.State switch
    {
        McpState.Connected => "Connected",
        McpState.NeedsAuth => "Needs sign-in",
        McpState.Failed => "Failed",
        McpState.NotSetUp => "Not set up",
        McpState.Pending => "Pending approval",
        _ => Server.Status,
    };
    public string Action => Server.State switch { McpState.NeedsAuth => "Sign in", McpState.Failed => "Retry", _ => "" };
    public Visibility ActionVisibility => Action.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveVisibility => RemoveScope is null ? Visibility.Collapsed : Visibility.Visible;
}
