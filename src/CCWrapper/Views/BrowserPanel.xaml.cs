using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CCWrapper.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CCWrapper.Views;

public partial class BrowserPanel : UserControl
{
    string? _pending; // a Navigate() that came before the WebView2 was ready

    public BrowserPanel() : this(null) { }

    /// <param name="userDataFolder">Null: this account's own profile.</param>
    public BrowserPanel(string? userDataFolder)
    {
        InitializeComponent();
        // its own profile per account, so each account keeps its own cookies and parallel windows don't clash
        Web.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = userDataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CCWrapper", "WebView2", "browser-" + AppState.Current.Account.Letter),
        };
        Web.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (!e.IsSuccess)
            {
                var why = "WebView2 failed to start: " + e.InitializationException?.Message;
                Address.Text = why;
                _ready.TrySetException(new InvalidOperationException("The Browser panel's " + why));
                return;
            }
            _ready.TrySetResult();
            // target=_blank and window.open stay in this single view; a page's window.open('file:...') is dropped, not opened
            Web.CoreWebView2.NewWindowRequested += (_, n) => { n.Handled = true; if (SchemeAllowed(n.Uri)) Web.CoreWebView2.Navigate(n.Uri); };
            if (_pending is { } url) Web.CoreWebView2.Navigate(url);
            _pending = null;
        };
        Web.SourceChanged += (_, _) => { if (!Address.IsKeyboardFocusWithin) Address.Text = Web.Source?.ToString(); };
        Loaded += async (_, _) =>
        {
            try { await Web.EnsureCoreWebView2Async(); } // no-op once started
            catch (Exception) { } // reported through CoreWebView2InitializationCompleted
        };
    }

    /// <summary>Called by MainWindow's IAppCommands.OpenInBrowser; keep this signature. Works before the WebView2 has started.</summary>
    public void Navigate(string url)
    {
        url = NormalizeUrl(url);
        Address.Text = url;
        if (Web.CoreWebView2 is { } core) core.Navigate(url);
        else _pending = url;
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

    /// <summary>Null when Claude's tools may work on the page at <paramref name="source"/>; otherwise the refusal Claude reads.</summary>
    public static string? NotAWebPage(string? source) => SchemeAllowed(source ?? "") ? null
        : $"The Browser panel shows a page that is not a web address ({(source?.IndexOf(':') is > 0 and var c ? source[..c] : "")}:); Claude's tools work only on web pages. Navigate to a web address first.";

    void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Address.Text.Trim().Length > 0) { Navigate(Address.Text); Web.Focus(); }
    }

    void OnAddressFocus(object sender, KeyboardFocusChangedEventArgs e) => Address.SelectAll();
    void OnBack(object sender, RoutedEventArgs e) => Web.GoBack();
    void OnForward(object sender, RoutedEventArgs e) => Web.GoForward();
    void OnReload(object sender, RoutedEventArgs e) => Web.Reload();
}
