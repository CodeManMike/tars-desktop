namespace TarsClient.Models;

/// <summary>
/// Everything the client remembers between runs, stored as <c>%APPDATA%\TARS\client.json</c> (spec section 8).
/// </summary>
public sealed class ClientSettings
{
    #region Connection

    /// <summary>The server's WebSocket endpoint.</summary>
    public string ServerUrl { get; set; } = "wss://192.168.86.243:8765/ws";

    /// <summary>The access key in plain text. We only ever hold it in memory; on disk it lives in <see cref="AccessKeyProtected"/>.</summary>
    public string AccessKey { get; set; } = "";

    /// <summary>The access key sealed with DPAPI (CurrentUser).</summary>
    public string AccessKeyProtected { get; set; } = "";

    #endregion

    #region Listening

    /// <summary>Listening mode: <c>ptt</c>, <c>wake</c> or <c>open</c>.</summary>
    public string Mode { get; set; } = "wake";

    /// <summary>Hold-to-talk key, as a WPF <c>Key</c> name.</summary>
    public string PttKey { get; set; } = "RightCtrl";

    /// <summary>Whether the PTT key is hidden from other apps while TARS uses it.</summary>
    public bool PttSwallow { get; set; }

    /// <summary>Global stop / dismiss combination, e.g. <c>Ctrl+Alt+S</c>.</summary>
    public string StopHotkey { get; set; } = "Ctrl+Alt+S";

    /// <summary>Mic muted inside TARS (not in Windows).</summary>
    public bool MicMuted { get; set; }

    /// <summary>How long the mic stays deaf after TARS stops talking, so it never hears itself.</summary>
    public int EchoTailMs { get; set; } = 300;

    #endregion

    #region Audio

    /// <summary>Output volume, 0–1.5.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>Capture endpoint id; empty means the Windows default.</summary>
    public string InputDevice { get; set; } = "";

    /// <summary>Render endpoint id; empty means the Windows default.</summary>
    public string OutputDevice { get; set; } = "";

    /// <summary>Whether typed messages are answered out loud.</summary>
    public bool SpeakTyped { get; set; } = true;

    #endregion

    #region Window

    /// <summary>Always on top.</summary>
    public bool Topmost { get; set; } = true;

    /// <summary><c>compact</c> or <c>expanded</c>.</summary>
    public string Layout { get; set; } = "compact";

    /// <summary>Remembered position and size of each layout.</summary>
    public LayoutBounds Bounds { get; set; } = new();

    /// <summary>Background alpha (text stays solid). Below 1.0 the window is layered, which takes effect on restart.</summary>
    public double BackgroundOpacity { get; set; } = 1.0;

    /// <summary>Whether the window has a taskbar button (off means tray only).</summary>
    public bool ShowInTaskbar { get; set; } = true;

    /// <summary>Show the window when TARS answers while it's hidden in the tray.</summary>
    public bool PopOnReply { get; set; }

    /// <summary>Clicking TARS doesn't take keyboard focus from the app we're in (the prompt and settings still do).</summary>
    public bool NoActivate { get; set; } = true;

    #endregion

    #region Display

    /// <summary>CRT scanline overlay.</summary>
    public bool Scanlines { get; set; } = true;

    /// <summary>Phosphor glow on text.</summary>
    public bool Glow { get; set; } = true;

    /// <summary>Base font size in the compact layout (the expanded layout adds 2).</summary>
    public double FontSize { get; set; } = 18;

    /// <summary>Typewriter reveal speed, characters per second.</summary>
    public int TypewriterCps { get; set; } = 90;

    /// <summary>Show timings, tools and ignored sounds in the scrollback.</summary>
    public bool ShowDetails { get; set; }

    #endregion

    #region Startup

    /// <summary>Register TARS in the Windows Run key.</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Start hidden in the tray.</summary>
    public bool StartMinimized { get; set; }

    #endregion

    #region Voice and speech

    /// <summary>Local text-to-speech settings.</summary>
    public TtsSettings Tts { get; set; } = new();

    /// <summary>Local speech-to-text settings.</summary>
    public SttSettings Stt { get; set; } = new();

    #endregion
}
