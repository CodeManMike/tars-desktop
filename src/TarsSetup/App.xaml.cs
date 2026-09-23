using System.IO;
using System.Windows;

namespace TarsSetup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Uninstall mode: --uninstall, or simply being named TARS-Uninstall.exe.
        bool uninstall = e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)) ||
                         Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("uninstall", StringComparison.OrdinalIgnoreCase);
        bool quiet = e.Args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
        if (uninstall && Installer.RelaunchFromTempIfInside(e.Args))
        {
            Shutdown();
            return;
        }
        new SetupWindow(uninstall, quiet).Show();
    }
}
