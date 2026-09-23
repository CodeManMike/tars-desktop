using TarsClient.Native;

namespace TarsClient.Services;

/// <summary>
/// Game mode (spec 6.5): a fullscreen/exclusive app is running, or free VRAM is under 3 GB.
/// Polled every 5 s (two cheap system calls). Changed is raised on the UI thread.
/// </summary>
public sealed class GameMode : IDisposable
{
    const ulong MinFreeVram = 3UL << 30;

    readonly Func<bool> _enabled;
    readonly System.Windows.Threading.DispatcherTimer _timer;
    IntPtr _gpu;
    bool _nvml;
    int _hits;

    public bool Active { get; private set; }
    public string Reason { get; private set; } = "";
    public ulong FreeVram { get; private set; }
    public event Action? Changed;

    public GameMode(Func<bool> enabled)
    {
        _enabled = enabled;
        try { _nvml = Win32.NvmlInit() == 0 && Win32.NvmlDeviceGetHandleByIndex(0, out _gpu) == 0; }
        catch { _nvml = false; }
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() { Poll(); _timer.Start(); }

    void Poll()
    {
        string reason = "";
        if (_enabled())
        {
            try
            {
                if (Win32.SHQueryUserNotificationState(out var st) == 0 &&
                    st is Win32.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN or Win32.QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY)
                    reason = "fullscreen app";
            }
            catch { }
            if (_nvml)
            {
                try
                {
                    if (Win32.NvmlDeviceGetMemoryInfo(_gpu, out var mem) == 0)
                    {
                        FreeVram = mem.free;
                        if (reason == "" && mem.free < MinFreeVram) reason = $"VRAM {mem.free / (1 << 20)} MB free";
                    }
                }
                catch { _nvml = false; }
            }
        }

        // Enter immediately; leave only after 3 quiet polls (15 s) so alt-tabbing doesn't flap.
        bool want = reason != "";
        _hits = want ? 0 : _hits + 1;
        bool next = want || (Active && _hits < 3);
        if (want) Reason = reason;
        if (next == Active) return;
        Active = next;
        Log.Write($"game mode: {(Active ? "on (" + Reason + ")" : "off")}");
        Changed?.Invoke();
    }

    public void Dispose() => _timer.Stop();
}
