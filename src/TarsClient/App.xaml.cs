using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using TarsClient.Views;

namespace TarsClient;

/// <summary>Single instance (a named mutex), the tray icon and menu, startup and quit.</summary>
public partial class App : Application
{
    #region Fields

    private const string MutexName = @"Local\TARS.Desktop.SingleInstance";
    private const string ShowEventName = @"Local\TARS.Desktop.Show";

    private static readonly (string Key, string Label)[] Modes =
        [("ptt", "Push to talk"), ("wake", "Say \"TARS\""), ("open", "Always listening")];

    private readonly List<MenuItem> _modeItems = [];
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private TaskbarIcon? _tray;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private MenuItem? _topItem;
    private MenuItem? _muteItem;
    private bool _quitting;

    #endregion

    #region Protected Methods

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }
        ListenForShowRequests();
        HandleUnhandledExceptions();

        base.OnStartup(e);

        var store = new SettingsStore();
        store.Load();
        // Re-register on every start so the Run entry follows the exe after an update or reinstall.
        if (store.Current.StartWithWindows) SettingsStore.ApplyStartWithWindows(true, store.Current.StartMinimized);

        _vm = new MainViewModel(store);
        _window = new MainWindow(_vm);
        BuildTray(_vm);

        bool minimized = e.Args.Contains("--minimized") || store.Current.StartMinimized;
        if (!minimized) _window.Show();
        _vm.Start();
        Log.Write($"started (minimized={minimized})");
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    #endregion

    #region Private Methods

    /// <summary>Already running: we bring that one forward and leave.</summary>
    private static void SignalRunningInstance()
    {
        try
        {
            EventWaitHandle.OpenExisting(ShowEventName).Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance is still starting or already gone.
        }
    }

    private void ListenForShowRequests()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(() => _window?.ShowFromTray());
        })
        {
            IsBackground = true,
            Name = "TARS single-instance",
        }.Start();
    }

    /// <summary>An always-on app logs and carries on rather than vanishing from the tray.</summary>
    private void HandleUnhandledExceptions()
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("unhandled: " + ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("fatal: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Write("task: " + ex.Exception.GetBaseException().Message);
            ex.SetObserved();
        };
    }

    private void BuildTray(MainViewModel vm)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Show", () => _window!.ShowFromTray()));
        menu.Items.Add(ModeMenu(vm));
        _topItem = Item("Always on top", () => vm.TogglePinCommand.Execute(null));
        _muteItem = Item("Mute mic", () => vm.MicMuted = !vm.MicMuted);
        menu.Items.Add(_topItem);
        menu.Items.Add(_muteItem);
        menu.Items.Add(Item("Settings", () =>
        {
            _window!.ShowFromTray();
            _window.OpenSettings();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit", Quit));
        menu.Opened += (_, _) => SyncMenu(vm);

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
            if (_quitting) return;
            if (e.PropertyName is nameof(MainViewModel.StatusTag) or nameof(MainViewModel.AssistantName))
                _tray.ToolTipText = $"{vm.AssistantName} · {vm.StatusTag}";
        };
    }

    private MenuItem ModeMenu(MainViewModel vm)
    {
        var mode = new MenuItem { Header = "Mode" };
        foreach (var (key, label) in Modes)
        {
            var item = new MenuItem { Header = label, Tag = key };
            item.Click += (_, _) => vm.ApplyMode(key, send: true);
            _modeItems.Add(item);
            mode.Items.Add(item);
        }
        return mode;
    }

    private static MenuItem Item(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void SyncMenu(MainViewModel vm)
    {
        foreach (var item in _modeItems) item.IsChecked = (string)item.Tag == vm.Mode;
        _topItem!.IsChecked = vm.Topmost;
        _muteItem!.IsChecked = vm.MicMuted;
    }

    private void Quit()
    {
        _quitting = true;
        Log.Write("quit");
        _vm?.Shutdown();
        _tray?.Dispose();
        if (_window != null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        Shutdown();
    }

    #endregion
}
