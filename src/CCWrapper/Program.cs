namespace CCWrapper;

public static class Program
{
    /// <summary>Velopack first: when the installer or updater starts this exe with a hook argument, it handles it and exits
    /// before any window exists. Otherwise it returns at once and WPF starts as usual (<c>--smoke</c>, <c>--account x</c>).</summary>
    [STAThread]
    public static void Main()
    {
        // Off: each account window is its own process, and applying at startup would force-stop the others (and ignore the off switch).
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
