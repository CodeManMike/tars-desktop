using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using TarsClient.Services;
using TarsClient.ViewModels;
using TarsClient.Views;

namespace TarsClient;

/// <summary>Single instance (named mutex), tray icon and menu, startup and quit.</summary>
public partial class App : Application
{
    const string MutexName = @"Local\TARS.Desktop.SingleInstance";
    const string ShowEventName = @"Local\TARS.Desktop.Show";

    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    TaskbarIcon? _tray;
    MainViewModel? _vm;
    MainWindow? _window;
    readonly List<MenuItem> _modeItems = [];
    MenuItem? _topItem, _muteItem;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running: bring that one forward and leave.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(() => _window?.ShowFromTray());
        }) { IsBackground = true, Name = "TARS single-instance" }.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("unhandled: " + ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("fatal: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Write("task: " + ex.Exception.GetBaseException().Message); ex.SetObserved(); };

        base.OnStartup(e);

        var store = new SettingsStore();
        store.Load();
        // Re-register on every start so the Run entry follows the exe after an update or reinstall.
        if (store.Current.StartWithWindows) SettingsStore.ApplyStartWithWindows(true, store.Current.StartMinimized);

        _vm = new MainViewModel(store);
        _window = new MainWindow(_vm);
        BuildTray();

        bool minimized = e.Args.Contains("--minimized") || store.Current.StartMinimized;
        if (!minimized) _window.Show();
        _vm.Start();
        Log.Write($"started (minimized={minimized})");
    }

    void BuildTray()
    {
        var vm = _vm!;
        var menu = new ContextMenu();

        menu.Items.Add(Item("Show", () => _window!.ShowFromTray()));

        var mode = new MenuItem { Header = "Mode" };
        foreach (var (key, label) in new[] { ("ptt", "Push to talk"), ("wake", "Say \"TARS\""), ("open", "Always listening") })
        {
            var mi = new MenuItem { Header = label, Tag = key, IsCheckable = false };
            mi.Click += (_, _) => vm.ApplyMode(key, send: true);
            _modeItems.Add(mi);
            mode.Items.Add(mi);
        }
        menu.Items.Add(mode);

        _topItem = Item("Always on top", () => vm.TogglePinCommand.Execute(null));
        _muteItem = Item("Mute mic", () => vm.MicMuted = !vm.MicMuted);
        menu.Items.Add(_topItem);
        menu.Items.Add(_muteItem);
        menu.Items.Add(Item("Settings", () => { _window!.ShowFromTray(); _window.OpenSettings(); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit", Quit));
        menu.Opened += (_, _) => SyncMenu();

        _tray = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/tars.ico")),
            ToolTipText = "TARS · NO CARRIER",
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _tray.TrayLeftMouseUp += (_, _) => _window!.ToggleVisible();
        // No efficiency mode: EcoQoS would throttle the mic and hotkey threads.
        _tray.ForceCreate(false);

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.StatusTag) or nameof(MainViewModel.AssistantName))
                _tray.ToolTipText = $"{vm.AssistantName} · {vm.StatusTag}";
        };
    }

    static MenuItem Item(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    void SyncMenu()
    {
        var vm = _vm!;
        foreach (var mi in _modeItems) mi.IsChecked = (string)mi.Tag == vm.Mode;
        _topItem!.IsChecked = vm.Topmost;
        _muteItem!.IsChecked = vm.MicMuted;
    }

    void Quit()
    {
        Log.Write("quit");
        _vm?.Shutdown();
        _tray?.Dispose();
        if (_window != null) { _window.AllowClose = true; _window.Close(); }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
