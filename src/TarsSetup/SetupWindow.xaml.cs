using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TarsSetup;

/// <summary>The one-screen installer/uninstaller: options, then a log while it works.</summary>
public partial class SetupWindow : Window
{
    #region Fields

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;

    private readonly bool _uninstall;
    private readonly bool _quiet;
    private readonly DispatcherTimer _spinner = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _busy;
    private bool _done;
    private int _spin;

    #endregion

    #region Constructor

    /// <summary>Creates the window; with <paramref name="quiet"/> it starts at once with the defaults and closes when done.</summary>
    public SetupWindow(bool uninstall, bool quiet = false)
    {
        _uninstall = uninstall;
        _quiet = quiet;
        if (quiet) Loaded += (_, _) => Go_Click(this, new RoutedEventArgs());
        InitializeComponent();
        VersionRun.Text = $"  v{Installer.Version}";
        TargetRun.Text = Installer.InstallDir;
        SourceInitialized += (_, _) => SquareCorners();
        _spinner.Tick += (_, _) => Status.Text = $"WORKING {"|/-\\"[_spin++ % 4]}";
        if (uninstall) ShowUninstall();
        else ShowInstall();
    }

    #endregion

    #region Private Methods

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void SquareCorners()
    {
        int preference = DWMWCP_DONOTROUND;
        try
        {
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch (EntryPointNotFoundException)
        {
            // Before Windows 11: corners are square anyway.
        }
    }

    private void ShowUninstall()
    {
        Title = "TARS Uninstall";
        Subtitle.Text = "UNINSTALL THE DESKTOP CLIENT";
        StartupBox.Visibility = DesktopBox.Visibility = VoiceBox.Visibility = LaunchBox.Visibility = Visibility.Collapsed;
        RemoveDataBox.Visibility = Visibility.Visible;
        GoButton.Content = "UNINSTALL";
        Note.Text = "Settings (%APPDATA%\\TARS) and the voice engine are kept unless you tick the box above.";
    }

    private void ShowInstall()
    {
        if (Installer.VoiceInstalled)
        {
            VoiceBox.IsChecked = false;
            VoiceBox.Content = "LOCAL VOICE ENGINE: ALREADY INSTALLED (tick to update)";
        }
        Note.Text = Installer.IsInstalled
            ? "An existing install will be replaced. Your settings and voice engine are kept."
            : "Per-user install, no admin needed. All of TARS's voice is generated on this PC.";
        GoButton.Content = Installer.IsInstalled ? "UPGRADE" : "INSTALL";
    }

    private void Log(string line) => Dispatcher.BeginInvoke(() =>
    {
        LogText.Text += (LogText.Text.Length > 0 ? "\n" : "") + line;
        LogScroll.ScrollToEnd();
    });

    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            Close();
            return;
        }
        if (_busy) return;
        _busy = true;
        GoButton.IsEnabled = CancelButton.IsEnabled = false;
        OptionsPanel.Visibility = Visibility.Collapsed;
        LogScroll.Visibility = Visibility.Visible;
        _spinner.Start();
        try
        {
            await RunAsync();
            Status.Text = _uninstall ? "UNINSTALLED" : "INSTALLED";
        }
        catch (Exception ex)
        {
            // The last line of defence for an interactive tool: whatever broke, show it rather than vanish.
            Log("ERROR: " + ex.Message);
            Status.Text = "FAILED";
        }
        finally
        {
            _spinner.Stop();
        }
        _busy = false;
        _done = true;
        if (_quiet)
        {
            await Task.Delay(1500);
            Close();
            return;
        }
        GoButton.Content = "CLOSE";
        GoButton.IsEnabled = true;
    }

    private Task RunAsync() => _uninstall
        ? Installer.UninstallAsync(RemoveDataBox.IsChecked == true, Log)
        : Installer.InstallAsync(new InstallOptions(StartupBox.IsChecked == true, DesktopBox.IsChecked == true, VoiceBox.IsChecked == true, LaunchBox.IsChecked == true), Log);

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }

    #endregion
}
