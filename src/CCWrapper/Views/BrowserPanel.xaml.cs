using System.Windows.Controls;

namespace CCWrapper.Views;

public partial class BrowserPanel : UserControl
{
    public BrowserPanel() => InitializeComponent();

    /// <summary>Called by MainWindow's IAppCommands.OpenInBrowser; keep this signature.</summary>
    public void Navigate(string url) { } // TODO(T7)
}
