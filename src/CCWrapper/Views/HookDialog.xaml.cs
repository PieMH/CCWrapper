using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;

namespace CCWrapper.Views;

/// <summary>Event, matcher, command and timeout of one command hook. The values are read after OK closes it.</summary>
public partial class HookDialog : Window
{
    public HookDialog(IEnumerable<string> events, string ev, string? matcher, string? command, double? timeout)
    {
        InitializeComponent();
        Title = string.IsNullOrEmpty(command) ? "Add hook" : "Edit hook";
        EventBox.ItemsSource = events;
        EventBox.Text = ev;
        MatcherBox.Text = matcher ?? "";
        CommandBox.Text = command ?? "";
        TimeoutBox.Text = timeout?.ToString(CultureInfo.InvariantCulture) ?? "";
        Loaded += (_, _) => CommandBox.Focus();
    }

    public string Event => EventBox.Text.Trim();
    public string? Matcher => MatcherBox.Text.Trim() is { Length: > 0 } m ? m : null;
    public string Command => CommandBox.Text.Trim();
    public double? Timeout { get; private set; }

    void OnOk(object sender, RoutedEventArgs e)
    {
        string? error = !Regex.IsMatch(Event, "^[A-Za-z]+$") ? "The event is one word, like PreToolUse."
            : Command.Length == 0 ? "Enter the command to run." : null;
        double? timeout = null;
        if (error is null && TimeoutBox.Text.Trim() is { Length: > 0 } text)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0) timeout = seconds;
            else error = "The timeout is a number of seconds, like 30.";
        }
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Timeout = timeout;
        DialogResult = true;
    }
}
