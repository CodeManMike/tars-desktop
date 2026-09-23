using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TarsSetup;

public partial class SetupWindow : Window
{
    readonly bool _uninstall;
    bool _quiet;
    bool _busy, _done;
    readonly DispatcherTimer _spinner = new() { Interval = TimeSpan.FromMilliseconds(120) };
    int _spin;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public SetupWindow(bool uninstall, bool quiet = false)
    {
        _uninstall = uninstall;
        // --quiet: run straight away with the defaults, close when done (Apps & Features "quiet uninstall").
        _quiet = quiet;
        if (quiet) Loaded += (_, _) => { Go_Click(this, new RoutedEventArgs()); };
        InitializeComponent();
        VersionRun.Text = $"  v{Installer.Version}";
        TargetRun.Text = Installer.InstallDir;
        SourceInitialized += (_, _) =>
        {
            int round = 1;   // DWMWCP_DONOTROUND: square corners
            try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int)); } catch { }
        };
        _spinner.Tick += (_, _) => Status.Text = $"WORKING {"|/-\\"[_spin++ % 4]}";

        if (uninstall)
        {
            Title = "TARS Uninstall";
            Subtitle.Text = "UNINSTALL THE DESKTOP CLIENT";
            StartupBox.Visibility = DesktopBox.Visibility = VoiceBox.Visibility = LaunchBox.Visibility = Visibility.Collapsed;
            RemoveDataBox.Visibility = Visibility.Visible;
            GoButton.Content = "UNINSTALL";
            Note.Text = "Settings (%APPDATA%\\TARS) and the voice engine are kept unless you tick the box above.";
        }
        else
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
    }

    void Log(string line) => Dispatcher.BeginInvoke(() =>
    {
        LogText.Text += (LogText.Text.Length > 0 ? "\n" : "") + line;
        LogScroll.ScrollToEnd();
    });

    async void Go_Click(object sender, RoutedEventArgs e)
    {
        if (_done) { Close(); return; }
        if (_busy) return;
        _busy = true;
        GoButton.IsEnabled = CancelButton.IsEnabled = false;
        OptionsPanel.Visibility = Visibility.Collapsed;
        LogScroll.Visibility = Visibility.Visible;
        _spinner.Start();
        try
        {
            if (_uninstall)
                await Installer.UninstallAsync(RemoveDataBox.IsChecked == true, Log);
            else
                await Installer.InstallAsync(new Installer.Options(
                    StartupBox.IsChecked == true, DesktopBox.IsChecked == true, VoiceBox.IsChecked == true, LaunchBox.IsChecked == true), Log);
            _spinner.Stop();
            Status.Text = _uninstall ? "UNINSTALLED" : "INSTALLED";
        }
        catch (Exception ex)
        {
            _spinner.Stop();
            Log("ERROR: " + ex.Message);
            Status.Text = "FAILED";
        }
        _busy = false;
        _done = true;
        if (_quiet) { await Task.Delay(1500); Close(); return; }
        GoButton.Content = "CLOSE";
        GoButton.IsEnabled = true;
    }

    void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Close();
    }
}
