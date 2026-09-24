namespace TarsClient.Services;

/// <summary>
/// Game mode (spec 6.5): a game has the screen and the GPU, so the voice moves to the CPU. We poll every 5 s
/// (two cheap system calls). <see cref="Changed"/> is raised on the UI thread.
/// </summary>
public sealed class GameMode : IDisposable
{
    #region Fields

    private const ulong MinFreeVram = 3UL << 30;
    private const int EnterPolls = 4;          // ~20 s: a screenshot overlay or a quick fullscreen video isn't a game
    private const int LeavePolls = 3;          // ~15 s: alt-tabbing out of a game doesn't flap
    private const uint BusyGpuPercent = 50;

    private readonly Func<bool> _enabled;
    private readonly DispatcherTimer _timer;
    private readonly IntPtr _gpu;
    private bool _nvml;
    private int _quietPolls, _busyPolls;

    #endregion

    #region Constructor

    /// <summary>Creates the detector; <paramref name="enabled"/> is read on every poll.</summary>
    public GameMode(Func<bool> enabled)
    {
        _enabled = enabled;
        try { _nvml = Win32.NvmlInit() == 0 && Win32.NvmlDeviceGetHandleByIndex(0, out _gpu) == 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { _nvml = false; }
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Poll();
    }

    #endregion

    #region Properties

    /// <summary>Whether game mode is on.</summary>
    public bool Active { get; private set; }

    /// <summary>Why it last switched on.</summary>
    public string Reason { get; private set; } = "";

    #endregion

    #region Events

    /// <summary>Game mode switched on or off.</summary>
    public event Action? Changed;

    #endregion

    #region Public Methods

    /// <summary>Polls now and every 5 s after.</summary>
    public void Start()
    {
        Poll();
        _timer.Start();
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Stop();

    #endregion

    #region Private Methods

    private void Poll()
    {
        string reason = _enabled() ? DetectGame() : "";
        bool want = reason != "";
        _quietPolls = want ? 0 : _quietPolls + 1;
        _busyPolls = want ? _busyPolls + 1 : 0;
        if (want) Reason = reason;

        bool next = Active ? _quietPolls < LeavePolls : _busyPolls >= EnterPolls;
        if (next == Active) return;
        Active = next;
        Log.Write($"game mode: {(Active ? "on (" + Reason + ")" : "off")}");
        Changed?.Invoke();
    }

    /// <summary>
    /// Exclusive D3D fullscreen is a game. "Busy" fullscreen (borderless games, but also a fullscreen video or a
    /// screenshot overlay) only counts while the GPU is actually working hard. Low free VRAM counts on its own.
    /// </summary>
    private string DetectGame()
    {
        if (Win32.SHQueryUserNotificationState(out var state) == 0)
        {
            switch (state)
            {
                case Win32.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN:
                    return "fullscreen game";
                case Win32.QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY when GpuBusy():
                    return "fullscreen app, GPU busy";
            }
        }
        if (!_nvml) return "";
        try
        {
            if (Win32.NvmlDeviceGetMemoryInfo(_gpu, out var mem) == 0 && mem.free < MinFreeVram)
                return $"VRAM {mem.free / (1 << 20)} MB free";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { _nvml = false; }
        return "";
    }

    private bool GpuBusy()
    {
        if (!_nvml) return true;         // we can't tell: assume a game
        try { return Win32.NvmlDeviceGetUtilizationRates(_gpu, out var util) == 0 && util.gpu >= BusyGpuPercent; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return true; }
    }

    #endregion
}
