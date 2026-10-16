using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CCWrapper.Views;

/// <summary>A message box in the app's theme: the buttons, icons and results of <see cref="MessageBox"/>, which is drawn by
/// Windows and ignores WPF styles. Enter picks the first button, Esc or closing the window the last; Ctrl+C copies the text.</summary>
public partial class Dialog : Window
{
    MessageBoxResult _result;

    public Dialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
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
        foreach (var (label, result) in choices)
        {
            var first = result == choices[0].Result;
            var button = new Button
            {
                Content = label, IsDefault = first, IsCancel = result == choices[^1].Result, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0),
            };
            if (first) button.Style = (Style)FindResource("Primary");
            else button.Padding = new Thickness(16, 4, 16, 4);
            button.Click += (_, _) => { _result = result; Close(); };
            Buttons.Children.Add(button);
        }
        Loaded += (_, _) => ((Button)Buttons.Children[0]).Focus();
    }

    /// <summary>Shows the dialog over <paramref name="owner"/> (centered on the screen when it is null or hidden) and returns
    /// the button picked, as <see cref="MessageBox.Show(Window, string, string, MessageBoxButton, MessageBoxImage)"/> does.</summary>
    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new Dialog(message, title, buttons, image);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._result;
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        try { Clipboard.SetText($"{Title}\n\n{Message.Text}"); }
        catch (ExternalException) { } // clipboard held by another app
    }
}
