namespace TarsSetup;

/// <summary>
/// Setup and uninstall in one exe: <c>--uninstall</c>, or simply being named <c>TARS-Uninstall.exe</c>, selects
/// uninstall; <c>--quiet</c> runs with the defaults and closes when done.
/// </summary>
public partial class App : Application
{
    #region Protected Methods

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool uninstall = HasFlag(e.Args, "--uninstall") ||
                         Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("uninstall", StringComparison.OrdinalIgnoreCase);
        bool quiet = HasFlag(e.Args, "--quiet");
        if (uninstall && Installer.RelaunchFromTempIfInside(e.Args))
        {
            Shutdown();
            return;
        }
        new SetupWindow(uninstall, quiet).Show();
    }

    #endregion

    #region Private Methods

    private static bool HasFlag(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    #endregion
}
