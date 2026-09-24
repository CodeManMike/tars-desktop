using System.Runtime.InteropServices;
using System.Windows.Input;

namespace TarsClient.Services;

/// <summary>
/// Global hold-to-talk and stop hotkey via <c>WH_KEYBOARD_LL</c>, so they work while a game has focus. Keyboard only:
/// we never hook the mouse (Michael's side buttons are used elsewhere). The hook lives on its own thread with its own
/// message loop, so a busy UI thread can't get it dropped. Events are raised on the UI thread. Keys are passed on
/// unless <see cref="Swallow"/> is set.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    #region Fields

    private const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5,
                      VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private readonly Dispatcher _ui;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private Win32.HookProc? _proc;          // held so the delegate isn't collected while Windows calls it
    private volatile int _pttVk;
    private volatile int _stopVk;
    private volatile int _stopMods;
    private volatile bool _pttDown;
    private volatile Action<string>? _capture;
    private int _mods;

    #endregion

    #region Constructor

    /// <summary>Creates the service; events are raised on <paramref name="ui"/>.</summary>
    public HotkeyService(Dispatcher ui) => _ui = ui;

    #endregion

    #region Properties

    /// <summary>Hide the PTT key from other apps (never applied to modifier keys, which would stick).</summary>
    public bool Swallow { get; set; }

    /// <summary>Whether the hotkeys act at all.</summary>
    public bool Enabled { get; set; } = true;

    #endregion

    #region Events

    /// <summary>The PTT key went down.</summary>
    public event Action? PttDown;

    /// <summary>The PTT key came up.</summary>
    public event Action? PttUp;

    /// <summary>The stop combination was pressed.</summary>
    public event Action? StopPressed;

    #endregion

    #region Public Methods

    /// <summary>Installs the hook on its own thread (idempotent).</summary>
    public void Start()
    {
        if (_thread != null) return;
        var ready = new ManualResetEventSlim();       // not disposed: the hook thread may set it after our wait times out
        _thread = new Thread(() => HookThread(ready)) { IsBackground = true, Name = "TARS hotkeys", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(2000);
    }

    /// <summary>Sets the PTT key and the stop combination.</summary>
    public void Configure(string pttKey, string stopHotkey)
    {
        _pttVk = ParseKey(pttKey);
        (_stopVk, _stopMods) = ParseCombo(stopHotkey);
    }

    /// <summary>The next key pressed anywhere is reported (and swallowed) instead of acted on.</summary>
    public void CaptureNext(Action<string> onKey) => _capture = onKey;

    /// <summary>Cancels <see cref="CaptureNext"/>.</summary>
    public void CancelCapture() => _capture = null;

    /// <summary>A WPF <c>Key</c> name ("RightCtrl", "F13") → virtual key. Anything unknown, including old "Mouse4" settings, means Right Ctrl.</summary>
    public static int ParseKey(string name) =>
        Enum.TryParse<Key>(name, true, out var key) ? KeyInterop.VirtualKeyFromKey(key) : VK_RCONTROL;

    /// <summary>"Ctrl+Alt+S" → (virtual key, modifier bits: 1 Ctrl, 2 Alt, 4 Shift, 8 Win).</summary>
    public static (int vk, int mods) ParseCombo(string combo)
    {
        int mods = 0, vk = 0;
        foreach (var part in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= 1; break;
                case "alt": mods |= 2; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                default: vk = Enum.TryParse<Key>(part, true, out var key) ? KeyInterop.VirtualKeyFromKey(key) : 0; break;
            }
        }
        return (vk, mods);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    #endregion

    #region Private Methods

    private void HookThread(ManualResetEventSlim ready)
    {
        _threadId = Win32.GetCurrentThreadId();
        _proc = KeyboardProc;
        _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write($"hotkeys: keyboard hook failed ({Marshal.GetLastWin32Error()})");
        ready.Set();
        while (Win32.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        Win32.UnhookWindowsHookEx(_hook);
    }

    private static int ModifierBit(int vk) => vk switch
    {
        VK_LCONTROL or VK_RCONTROL => 1,
        VK_LMENU or VK_RMENU => 2,
        VK_LSHIFT or VK_RSHIFT => 4,
        VK_LWIN or VK_RWIN => 8,
        _ => 0,
    };

    private static string KeyName(int vk) => KeyInterop.KeyFromVirtualKey(vk).ToString();

    private string ComboName(int vk)
    {
        var parts = new List<string>();
        if ((_mods & 1) != 0) parts.Add("Ctrl");
        if ((_mods & 2) != 0) parts.Add("Alt");
        if ((_mods & 4) != 0) parts.Add("Shift");
        if ((_mods & 8) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
        int msg = (int)wParam;
        bool down = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
        bool up = msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;
        int vk = (int)info.vkCode;
        int bit = ModifierBit(vk);

        if (down && TryCapture(vk, bit)) return 1;
        if (bit != 0 && (down || up)) _mods = down ? _mods | bit : _mods & ~bit;
        if (!Enabled) return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

        bool swallow = vk == _pttVk ? HandlePtt(down, up, bit) : down && HandleStop(vk);
        return swallow ? 1 : Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>In capture mode, a lone modifier waits for the real key, unless it's the whole binding (Right Ctrl/Alt for PTT).</summary>
    private bool TryCapture(int vk, int bit)
    {
        if (_capture is not { } capture) return false;
        if (bit != 0 && vk is not (VK_RCONTROL or VK_RMENU)) return false;

        var name = bit == 0 ? ComboName(vk) : KeyName(vk);
        _capture = null;
        _ui.BeginInvoke(() => capture(name));
        return true;
    }

    private bool HandlePtt(bool down, bool up, int bit)
    {
        if (down && !_pttDown)
        {
            _pttDown = true;
            _ui.BeginInvoke(() => PttDown?.Invoke());
        }
        else if (up && _pttDown)
        {
            _pttDown = false;
            _ui.BeginInvoke(() => PttUp?.Invoke());
        }
        return Swallow && bit == 0;
    }

    private bool HandleStop(int vk)
    {
        if (vk != _stopVk || _stopVk == 0 || (_mods & 0xF) != _stopMods) return false;
        _ui.BeginInvoke(() => StopPressed?.Invoke());
        return true;
    }

    #endregion
}
