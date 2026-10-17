using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CCWrapper.Core;
using CCWrapper.Data;

namespace CCWrapper.Views;

/// <summary>The right-click menu of a session card, shared by the sidebar rows and the tab cards: Name and Color go to the
/// TUI as /rename and /color through <see cref="TuiInput"/> (so they need the session open in this window), Delete removes
/// the transcript of a session nothing has open.</summary>
static class SessionMenu
{
    /// <param name="live">The tab in this window, or null.</param>
    /// <param name="entry">The transcript, or null when the card has none at hand.</param>
    /// <param name="color">The session's /color ("default" when none), or null when not known: nothing is checked.</param>
    /// <param name="deleted">Called after a delete went through, to refresh the list.</param>
    public static void Open(FrameworkElement target, LiveSession? live, SessionEntry? entry, string title, string? color, Action deleted)
    {
        var owner = Window.GetWindow(target);
        var rename = Item("Name…", live is null ? "Open the session to rename it" : null, async () =>
        {
            if (ShellModel.RenameCommand(Dialog.Prompt(owner, "Name this session", "Name", title)) is { } command) Refused(owner, await TuiInput.SubmitAsync(live, command), "Could not rename");
        });
        var colors = Item("Color", live is null ? "Open the session to change its color" : null, null);
        foreach (var name in ShellModel.SessionColors)
            colors.Items.Add(ColorItem(name, name == color, async () => Refused(owner, await TuiInput.SubmitAsync(live, "/color " + name), "Could not change the color")));
        var delete = Item("Delete…", ShellModel.DeleteBlock(live, entry), async () =>
        {
            if (entry is null || Dialog.Show(owner, $"Delete “{title}”?\n\nIts transcript is deleted for good and the session cannot be resumed.", "Delete session",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var error = await Task.Run(() => SessionCatalog.Delete(entry));
            if (error is null) deleted();
            else Dialog.Show(owner, error, "Could not delete the session", MessageBoxButton.OK, MessageBoxImage.Error);
        });
        var menu = new ContextMenu { PlacementTarget = target, Items = { rename, colors, new Separator(), delete } };
        menu.IsOpen = true;
    }

    // The compose notice only shows a refusal for the active tab, and a card's tab may be another one.
    static void Refused(Window? owner, string? reason, string title)
    {
        if (reason is not null) Dialog.Show(owner, reason, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    static MenuItem Item(string header, string? blocked, Func<Task>? click)
    {
        var item = new MenuItem { Header = header, IsEnabled = blocked is null, ToolTip = blocked };
        ToolTipService.SetShowOnDisabled(item, true);
        if (click is not null) item.Click += async (_, _) => await click();
        return item;
    }

    static MenuItem ColorItem(string name, bool current, Func<Task> click)
    {
        var swatch = new Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        if (name != "default") swatch.Fill = (Brush)Application.Current.FindResource("Session" + char.ToUpperInvariant(name[0]) + name[1..]);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, new TextBlock { Text = name } } };
        if (current)
            header.Children.Add(new TextBlock { Text = "", Style = (Style)Application.Current.FindResource("Icon"), FontSize = 11, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await click();
        return item;
    }
}
