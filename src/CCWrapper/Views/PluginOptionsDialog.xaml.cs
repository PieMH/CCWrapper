using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CCWrapper.Data;

namespace CCWrapper.Views;

/// <summary>The options a plugin declares, one field each: a list when the option has choices (booleans too), a password box
/// when it is sensitive, else a text box.</summary>
public partial class PluginOptionsDialog : Window
{
    readonly List<(PluginOption Option, Func<string> Read)> _fields = [];

    /// <summary>Set when Save closes the dialog: the changed values, as <c>claude plugin configure --values-stdin</c> takes them.</summary>
    public Dictionary<string, string>? Changes { get; private set; }

    public PluginOptionsDialog(string pluginName, IReadOnlyList<PluginOption> options)
    {
        InitializeComponent();
        Title = $"Configure {pluginName}";
        Heading.Text = $"Options of {pluginName}";
        foreach (var o in options)
        {
            var label = o.Title + (o.Required ? " (required)" : "") + (o.Sensitive && o.IsSet ? " · set" : "");
            Fields.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 14, 0, 4) });
            if (o.Description is { Length: > 0 } description)
                Fields.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
            Control field;
            Func<string> read;
            if (o.Choices is { Count: > 0 } choices)
            {
                var box = new ComboBox { ItemsSource = choices, SelectedItem = choices.Contains(o.Value) ? o.Value : null };
                (field, read) = (box, () => box.SelectedItem as string ?? o.Value);
            }
            else if (o.Sensitive)
            {
                var box = new PasswordBox();
                (field, read) = (box, () => box.Password);
            }
            else
            {
                var box = new TextBox { Text = o.Value };
                (field, read) = (box, () => box.Text);
            }
            field.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, o.Title);
            Fields.Children.Add(field);
            _fields.Add((o, read));
        }
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        Changes = PluginData.ChangedOptions(_fields.Select(f => (f.Option, f.Read())));
        DialogResult = true;
    }
}
