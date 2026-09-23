using System.Windows;

namespace TarsSetup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool uninstall = e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        new SetupWindow(uninstall).Show();
    }
}
