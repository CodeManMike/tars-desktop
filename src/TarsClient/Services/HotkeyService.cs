using System.Runtime.InteropServices;
using System.Windows.Input;
using TarsClient.Native;

namespace TarsClient.Services;

/// <summary>
/// Global hold-to-talk and stop hotkey via WH_KEYBOARD_LL / WH_MOUSE_LL, so they work while a game has focus.
/// The hooks live on a dedicated thread with its own message loop: a busy UI thread can't get them dropped.
/// Events are raised on the UI thread. Keys are passed on unless <see cref="Swallow"/> is set.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5,
              VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    readonly System.Windows.Threading.Dispatcher _ui;
    Thread? _thread;
    uint _threadId;
    IntPtr _kbHook, _mouseHook;
    Win32.HookProc? _kbProc, _mouseProc;   // keep delegates alive

    volatile int _pttVk;            // virtual key, or -4 / -5 for mouse X buttons
    volatile int _stopVk;
    volatile int _stopMods;         // 1 ctrl, 2 alt, 4 shift, 8 win
    volatile bool _pttDown;
    volatile Action<string>? _capture;
    int _mods;

    public bool Swallow { get; set; }
    public bool Enabled { get; set; } = true;

    public event Action? PttDown;
    public event Action? PttUp;
    public event Action? StopPressed;

    public HotkeyService(System.Windows.Threading.Dispatcher ui) => _ui = ui;

    public void Start()
    {
        if (_thread != null) return;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Win32.GetCurrentThreadId();
            _kbProc = KeyboardProc;
            _mouseProc = MouseProc;
            var mod = Win32.GetModuleHandle(null);
            _kbHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _kbProc, mod, 0);
            _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, mod, 0);
            if (_kbHook == IntPtr.Zero) Log.Write($"hotkeys: keyboard hook failed ({Marshal.GetLastWin32Error()})");
            ready.Set();
            while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { }
            Win32.UnhookWindowsHookEx(_kbHook);
            Win32.UnhookWindowsHookEx(_mouseHook);
        }) { IsBackground = true, Name = "TARS hotkeys", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(2000);
    }

    public void Configure(string pttKey, string stopHotkey)
    {
        _pttVk = ParseKey(pttKey);
        var (vk, mods) = ParseCombo(stopHotkey);
        _stopVk = vk;
        _stopMods = mods;
    }

    /// <summary>The next key or mouse side button pressed anywhere is reported (and swallowed) instead of acted on.</summary>
    public void CaptureNext(Action<string> onKey) => _capture = onKey;
    public void CancelCapture() => _capture = null;

    // "RightCtrl", "F13", "Mouse4", "Mouse5"
    public static int ParseKey(string name)
    {
        if (name.Equals("Mouse4", StringComparison.OrdinalIgnoreCase)) return -4;
        if (name.Equals("Mouse5", StringComparison.OrdinalIgnoreCase)) return -5;
        return Enum.TryParse<Key>(name, true, out var k) ? KeyInterop.VirtualKeyFromKey(k) : VK_RCONTROL;
    }

    // "Ctrl+Alt+S"
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
                default: vk = Enum.TryParse<Key>(part, true, out var k) ? KeyInterop.VirtualKeyFromKey(k) : 0; break;
            }
        }
        return (vk, mods);
    }

    static string KeyName(int vk) => KeyInterop.KeyFromVirtualKey(vk).ToString();

    string ComboName(int vk)
    {
        var parts = new List<string>();
        if ((_mods & 1) != 0) parts.Add("Ctrl");
        if ((_mods & 2) != 0) parts.Add("Alt");
        if ((_mods & 4) != 0) parts.Add("Shift");
        if ((_mods & 8) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    static int ModBit(int vk) => vk switch
    {
        VK_LCONTROL or VK_RCONTROL => 1,
        VK_LMENU or VK_RMENU => 2,
        VK_LSHIFT or VK_RSHIFT => 4,
        VK_LWIN or VK_RWIN => 8,
        _ => 0,
    };

    IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Win32.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        var k = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
        int msg = (int)wParam;
        bool down = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
        bool up = msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;
        int vk = (int)k.vkCode;
        int bit = ModBit(vk);

        if (_capture is { } cap && down)
        {
            // Modifier alone: wait for the real key, unless it's the whole binding (e.g. RightCtrl for PTT).
            if (bit == 0 || vk is VK_RCONTROL or VK_RMENU)
            {
                var name = bit == 0 ? ComboName(vk) : KeyName(vk);
                _capture = null;
                _ui.BeginInvoke(() => cap(name));
                return 1;
            }
        }

        if (bit != 0) _mods = down ? _mods | bit : up ? _mods & ~bit : _mods;
        if (!Enabled) return Win32.CallNextHookEx(_kbHook, nCode, wParam, lParam);

        bool swallow = false;
        if (vk == _pttVk)
        {
            if (down && !_pttDown) { _pttDown = true; _ui.BeginInvoke(() => PttDown?.Invoke()); }
            else if (up && _pttDown) { _pttDown = false; _ui.BeginInvoke(() => PttUp?.Invoke()); }
            swallow = Swallow && bit == 0;   // never swallow a modifier: it would stick
        }
        else if (down && vk == _stopVk && _stopVk != 0 && (_mods & 0xF) == _stopMods)
        {
            _ui.BeginInvoke(() => StopPressed?.Invoke());
            swallow = true;
        }
        return swallow ? 1 : Win32.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        int msg = (int)wParam;
        if (nCode < 0 || msg is not (Win32.WM_XBUTTONDOWN or Win32.WM_XBUTTONUP))
            return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        var m = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
        int button = (int)(m.mouseData >> 16) == 1 ? -4 : -5;
        bool down = msg == Win32.WM_XBUTTONDOWN;

        if (_capture is { } cap && down)
        {
            _capture = null;
            _ui.BeginInvoke(() => cap(button == -4 ? "Mouse4" : "Mouse5"));
            return 1;
        }
        if (!Enabled || button != _pttVk) return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        if (down && !_pttDown) { _pttDown = true; _ui.BeginInvoke(() => PttDown?.Invoke()); }
        else if (!down && _pttDown) { _pttDown = false; _ui.BeginInvoke(() => PttUp?.Invoke()); }
        return Swallow ? 1 : Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
