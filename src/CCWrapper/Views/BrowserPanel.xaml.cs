using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CCWrapper.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CCWrapper.Views;

public partial class BrowserPanel : UserControl
{
    /// <summary>One tab: its own WebView2, started on the panel's one environment, and its header in the strip.</summary>
    sealed class Tab(WebView2 web, RadioButton header, TextBlock title)
    {
        public int Id;
        public readonly WebView2 Web = web;
        public readonly RadioButton Header = header;
        public readonly TextBlock Title = title;
        public Task Ready = Task.CompletedTask;
        public string? Pending; // a Navigate() that came before this tab's WebView2 was ready
    }

    readonly string _profile;
    readonly BrowserTabs<Tab> _tabs = new();
    readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<CoreWebView2Environment>? _env;

    Tab Front => _tabs.FrontTab!;

    public BrowserPanel() : this(null) { }

    /// <param name="userDataFolder">Null: this account's own profile.</param>
    public BrowserPanel(string? userDataFolder)
    {
        InitializeComponent();
        // its own profile per account, so each account keeps its own cookies and parallel windows don't clash; every tab shares it
        _profile = userDataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCWrapper", "WebView2", "browser-" + AppState.Current.Account.Letter);
        Loaded += (_, _) => _loaded.TrySetResult();
        AddTab();
    }

    /// <summary>Called by MainWindow's IAppCommands.OpenInBrowser; keep this signature. Loads in the front tab; works before the WebView2 has started.</summary>
    public void Navigate(string url)
    {
        url = NormalizeUrl(url);
        Address.Text = url;
        Load(Front, url);
    }

    /// <summary>A new blank tab in front, or null at <see cref="BrowserTabs{T}.Max"/> tabs.</summary>
    Tab? AddTab()
    {
        if (_tabs.Full) return null;
        var title = new TextBlock { Text = "New tab", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button
        {
            Style = (Style)FindResource("Flat"), Width = 22, Height = 22, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Close tab", Content = new TextBlock { Style = (Style)FindResource("Icon"), Text = "", FontSize = 10 },
        };
        AutomationProperties.SetName(close, "Close tab");
        Grid.SetColumn(close, 1);
        var header = new RadioButton
        {
            Style = (Style)FindResource("BrowserTab"),
            Content = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { title, close } },
        };
        var tab = new Tab(new WebView2 { Visibility = Visibility.Collapsed }, header, title);
        tab.Id = _tabs.Add(tab);
        close.Click += (_, _) => CloseTab(tab.Id);
        header.Checked += (_, _) => Show(tab);
        tab.Web.SourceChanged += (_, _) => { if (tab == _tabs.FrontTab && !Address.IsKeyboardFocusWithin) Address.Text = tab.Web.Source?.ToString(); };
        Strip.Children.Add(header);
        Pages.Children.Add(tab.Web);
        Retitle(tab);
        tab.Ready = StartAsync(tab);
        Show(tab);
        return tab;
    }

    async Task StartAsync(Tab tab)
    {
        await _loaded.Task; // nothing starts before the panel is first shown
        try { await tab.Web.EnsureCoreWebView2Async(await (_env ??= CoreWebView2Environment.CreateAsync(null, _profile))); }
        catch (Exception ex)
        {
            var why = "WebView2 failed to start: " + ex.Message;
            if (tab == _tabs.FrontTab) Address.Text = why;
            throw new InvalidOperationException("The Browser panel's " + why, ex);
        }
        var core = tab.Web.CoreWebView2;
        core.NewWindowRequested += OnNewWindow;
        core.DocumentTitleChanged += (_, _) => Retitle(tab);
        if (tab.Pending is { } url) core.Navigate(url);
        tab.Pending = null;
    }

    /// <summary>target=_blank and window.open, from any tab: a new tab in front. A page's window.open('file:...') is dropped, not
    /// opened, and so is one past the tab limit.</summary>
    async void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!SchemeAllowed(e.Uri) || AddTab() is not { } tab) { e.Handled = true; return; }
        var deferral = e.GetDeferral();
        try
        {
            await tab.Ready.WaitAsync(StartLimit);
            e.NewWindow = tab.Web.CoreWebView2; // the same environment, not navigated yet: the opener keeps a window object for it
        }
        catch (Exception)
        {
            e.Handled = true;
            if (_tabs[tab.Id] == tab) Load(tab, e.Uri);
        }
        finally { deferral.Complete(); }
    }

    // Only once StartAsync has wired NewWindowRequested: before that the URL waits in Pending.
    static void Load(Tab tab, string url)
    {
        if (tab.Ready.IsCompletedSuccessfully) tab.Web.CoreWebView2.Navigate(url);
        else tab.Pending = url;
    }

    void Retitle(Tab tab)
    {
        var title = tab.Web.CoreWebView2?.DocumentTitle;
        tab.Title.Text = string.IsNullOrWhiteSpace(title) ? "New tab" : title;
        tab.Header.ToolTip = tab.Title.Text;
        AutomationProperties.SetName(tab.Header, tab.Title.Text);
    }

    void Show(Tab tab)
    {
        _tabs.Switch(tab.Id);
        foreach (var (_, t) in _tabs.All)
        {
            t.Web.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
            t.Header.IsChecked = t == tab;
        }
        Bar.DataContext = tab.Web;
        Address.Text = tab.Web.Source?.ToString() ?? tab.Pending ?? "";
    }

    /// <summary>Disposes the tab's WebView2; closing the last tab leaves one blank tab. False when there is no tab <paramref name="id"/>.</summary>
    bool CloseTab(int id)
    {
        if (_tabs[id] is not { } tab) return false;
        _tabs.Remove(id);
        Strip.Children.Remove(tab.Header);
        Pages.Children.Remove(tab.Web);
        tab.Web.Dispose();
        if (_tabs.FrontTab is { } front) Show(front);
        else AddTab();
        return true;
    }

    /// <summary>"localhost:3000" -> "http://localhost:3000", "example.com" -> "https://example.com"; anything with a scheme is kept.</summary>
    public static string NormalizeUrl(string input)
    {
        var s = input.Trim();
        if (s.Contains("://") || s.StartsWith("about:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return s;
        var host = s.Split('/', '?', '#')[0];
        var local = host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || host.StartsWith("127.") || host.StartsWith("0.0.0.0") || host.StartsWith("[::1]");
        return (local ? "http://" : "https://") + s;
    }

    /// <summary>Whether a page or Claude may open <paramref name="url"/>: http, https, blob, data and about:blank (any other about: page may
    /// be an internal edge:// page). The address bar is not gated.</summary>
    public static bool SchemeAllowed(string url)
    {
        var colon = url.IndexOf(':');
        if (colon <= 0) return false;
        return url[..colon].ToLowerInvariant() switch
        {
            "http" or "https" or "blob" or "data" => true,
            "about" => url[(colon + 1)..].Split('#')[0].Equals("blank", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>Null when Claude's <paramref name="tool"/> may open <paramref name="url"/> (already normalized); otherwise the refusal Claude reads.</summary>
    public static string? OpenRefusal(string tool, string url) => SchemeAllowed(url) ? null
        : $"Refused {url}: the {tool} tool opens only http, https, blob, about:blank and data addresses.";

    /// <summary>Null when Claude's tools may work on the page at <paramref name="source"/>; otherwise the refusal Claude reads.</summary>
    public static string? NotAWebPage(string? source) => SchemeAllowed(source ?? "") ? null
        : $"The Browser panel shows a page that is not a web address ({(source?.IndexOf(':') is > 0 and var c ? source[..c] : "")}:); Claude's tools work only on web pages. Navigate to a web address first.";

    /// <summary>The tabs tool's text, one line per tab. A tab whose page is not a web address shows neither its title nor its URL.</summary>
    public static string DescribeTabs(IEnumerable<(int Id, string? Title, string? Url, bool Front)> tabs) => string.Join("\n", tabs.Select(t =>
        $"Tab {t.Id}{(t.Front ? " (front)" : "")}: " + (NotAWebPage(t.Url) is null ? $"{t.Title} - {t.Url}" : "(not a web page)")));

    void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Address.Text.Trim().Length > 0) { Navigate(Address.Text); Front.Web.Focus(); }
    }

    void OnAddressFocus(object sender, KeyboardFocusChangedEventArgs e) => Address.SelectAll();
    void OnBack(object sender, RoutedEventArgs e) => Front.Web.GoBack();
    void OnForward(object sender, RoutedEventArgs e) => Front.Web.GoForward();
    void OnReload(object sender, RoutedEventArgs e) { if (Front.Ready.IsCompletedSuccessfully) Front.Web.Reload(); }
    void OnNewTab(object sender, RoutedEventArgs e) => AddTab();
}

/// <summary>The Browser panel's tab bookkeeping, without WPF: ids 1, 2, 3… never reused, which tab is in front, at most <see cref="Max"/>.</summary>
public sealed class BrowserTabs<T> where T : class
{
    public const int Max = 20;

    readonly List<(int Id, T Tab)> _all = [];
    int _last;

    /// <summary>In strip order.</summary>
    public IReadOnlyList<(int Id, T Tab)> All => _all;

    /// <summary>The front tab's id; 0 when there is none.</summary>
    public int Front { get; private set; }

    public T? FrontTab => this[Front];
    public bool Full => _all.Count >= Max;
    public T? this[int id] => _all.Find(t => t.Id == id).Tab;

    /// <summary>Appends <paramref name="tab"/> in front and returns its id; 0 when full.</summary>
    public int Add(T tab)
    {
        if (Full) return 0;
        _all.Add((++_last, tab));
        return Front = _last;
    }

    public bool Switch(int id)
    {
        if (this[id] is null) return false;
        Front = id;
        return true;
    }

    /// <summary>When the front tab goes, the one to its right comes to front, else the one to its left. False when there is no tab <paramref name="id"/>.</summary>
    public bool Remove(int id)
    {
        var i = _all.FindIndex(t => t.Id == id);
        if (i < 0) return false;
        _all.RemoveAt(i);
        if (Front == id) Front = _all.Count == 0 ? 0 : _all[Math.Min(i, _all.Count - 1)].Id;
        return true;
    }
}
