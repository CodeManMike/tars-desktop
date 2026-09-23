using System.Text.Json.Serialization;

namespace TarsClient.Models;

/// <summary>%APPDATA%\TARS\client.json (spec section 8).</summary>
public sealed class ClientSettings
{
    public string ServerUrl { get; set; } = "wss://192.168.86.243:8765/ws";
    /// <summary>Plain key: only ever in memory. On disk it's cleared and <see cref="AccessKeyProtected"/> holds it.</summary>
    public string AccessKey { get; set; } = "";
    public string Mode { get; set; } = "wake";
    public double Volume { get; set; } = 1.0;
    public bool Topmost { get; set; } = true;
    public string Layout { get; set; } = "compact";
    public LayoutBounds Bounds { get; set; } = new();
    public string PttKey { get; set; } = "RightCtrl";
    public bool PttSwallow { get; set; }
    public string StopHotkey { get; set; } = "Ctrl+Alt+S";
    public string InputDevice { get; set; } = "";
    public string OutputDevice { get; set; } = "";
    public int EchoTailMs { get; set; } = 300;
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool Scanlines { get; set; } = true;
    public bool Glow { get; set; } = true;
    public double FontSize { get; set; } = 18;
    public int TypewriterCps { get; set; } = 90;
    public bool ShowDetails { get; set; }
    public bool SpeakTyped { get; set; } = true;
    public bool MicMuted { get; set; }
    /// <summary>Background alpha (text stays solid). Below 1.0 the window is layered; takes effect on restart.</summary>
    public double BackgroundOpacity { get; set; } = 1.0;
    public bool ShowInTaskbar { get; set; } = true;
    /// <summary>Show the window when TARS answers while it's hidden in the tray.</summary>
    public bool PopOnReply { get; set; }
    /// <summary>Clicking TARS doesn't take keyboard focus from the app you're in (the prompt and settings still do).</summary>
    public bool NoActivate { get; set; } = true;
    public string VoiceEngine { get; set; } = "local";
    public TtsSettings Tts { get; set; } = new();
    public SttSettings Stt { get; set; } = new();
    public string AccessKeyProtected { get; set; } = "";
}

public sealed class LayoutBounds
{
    public double[] Compact { get; set; } = [1500, 60, 380, 150];
    public double[] Expanded { get; set; } = [1200, 60, 640, 520];
}

public sealed class TtsSettings
{
    public string SidecarUrl { get; set; } = "http://127.0.0.1:8880";
    public string Engine { get; set; } = "turbo";
    public string ReferenceClip { get; set; } = "";
    public double Exaggeration { get; set; } = 0.30;
    public double Pace { get; set; } = 0.35;
    public double Speed { get; set; } = 1.0;
    public double Temperature { get; set; } = 0.7;
    /// <summary>TARS FX varispeed: 1.0 = off, 0.95 = a touch deeper and slower.</summary>
    public double FxPitch { get; set; } = 0.95;
    /// <summary>TARS FX metallic ring (comb gain) 0..0.5.</summary>
    public double FxRing { get; set; } = 0.22;
    /// <summary>TARS FX: slight pitch drop plus a small metallic speaker colouring, applied on the client.</summary>
    public bool Fx { get; set; } = true;
    public bool GameMode { get; set; } = true;
    /// <summary>Always-on friendly default: free the GPU after 10 idle minutes.</summary>
    public int IdleUnloadMinutes { get; set; } = 10;
    public Dictionary<string, int> LabRatings { get; set; } = new();
}

public sealed class SttSettings
{
    /// <summary>"local": Whisper on this PC via the sidecar (falls back to the server when it's down). "server": stream audio as before.</summary>
    public string Mode { get; set; } = "local";
    public string Model { get; set; } = "large-v3-turbo";
    /// <summary>CPU model while a game has the GPU.</summary>
    public string GameModel { get; set; } = "small.en";
    /// <summary>Only your voice (voiceprint) starts hands-free turns; YouTube/TV/other people are dropped before Whisper.</summary>
    public bool VoiceLock { get; set; }
    public string Voiceprint { get; set; } = "";
    public double SpeakerThreshold { get; set; } = 0.72;
}

[JsonSerializable(typeof(ClientSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class SettingsJsonContext : JsonSerializerContext;
