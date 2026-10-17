using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CCWrapper.Views;

/// <summary>A message box in the app's theme: the buttons, icons and results of <see cref="MessageBox"/>, which is drawn by
/// Windows and ignores WPF styles. Enter picks the first button (or the one named as default), Esc or closing the window the last; Ctrl+C copies the text.</summary>
public partial class Dialog : Window
{
    MessageBoxResult _result;

    public Dialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultButton = MessageBoxResult.None)
    {
        InitializeComponent();
        Title = title;
        Message.Text = message;
        (Glyph.Text, Glyph.Foreground) = image switch
        {
            MessageBoxImage.Error => ("", (Brush)FindResource("Bad")),
            MessageBoxImage.Warning => ("", (Brush)FindResource("Warn")),
            MessageBoxImage.Question => ("", (Brush)FindResource("Accent")),
            MessageBoxImage.Information => ("", (Brush)FindResource("Accent")),
            _ => ("", null),
        };
        if (Glyph.Text.Length == 0) Glyph.Visibility = Visibility.Collapsed;

        (string Label, MessageBoxResult Result)[] choices = buttons switch
        {
            MessageBoxButton.OKCancel => [("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxButton.YesNo => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No)],
            MessageBoxButton.YesNoCancel => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel)],
            _ => [("OK", MessageBoxResult.OK)],
        };
        _result = choices[^1].Result;
        var preferred = choices.Any(c => c.Result == defaultButton) ? defaultButton : choices[0].Result;
        foreach (var (label, result) in choices)
        {
            var first = result == preferred;
            var button = new Button
            {
                Content = label, IsDefault = first, IsCancel = result == choices[^1].Result, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0),
            };
            if (first) button.Style = (Style)FindResource("Primary");
            else button.Padding = new Thickness(16, 4, 16, 4);
            button.Click += (_, _) => { _result = result; Close(); };
            Buttons.Children.Add(button);
        }
        Loaded += (_, _) => ((Button)Buttons.Children[Array.FindIndex(choices, c => c.Result == preferred)]).Focus();
    }

    /// <summary>Shows the dialog over <paramref name="owner"/> (centered on the screen when it is null or hidden) and returns
    /// the button picked, as <see cref="MessageBox.Show(Window, string, string, MessageBoxButton, MessageBoxImage)"/> does.</summary>
    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultButton = MessageBoxResult.None) =>
        Run(owner, new Dialog(message, title, buttons, image, defaultButton))._result;

    /// <summary>A one-line text prompt with OK and Cancel, <paramref name="initial"/> selected. The text as typed, null on Cancel.</summary>
    public static string? Prompt(Window? owner, string message, string title, string initial)
    {
        var dialog = new Dialog(message, title, MessageBoxButton.OKCancel, MessageBoxImage.None);
        dialog.Input.Text = initial;
        dialog.Input.Visibility = Visibility.Visible;
        dialog.Loaded += (_, _) => { dialog.Input.Focus(); dialog.Input.SelectAll(); };
        return Run(owner, dialog)._result == MessageBoxResult.OK ? dialog.Input.Text : null;
    }

    static Dialog Run(Window? owner, Dialog dialog)
    {
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog;
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control || Input.IsKeyboardFocusWithin) return; // the field copies its own selection
        e.Handled = true;
        try { Clipboard.SetText($"{Title}\n\n{Message.Text}"); }
        catch (ExternalException) { } // clipboard held by another app
    }
}
