using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TarsClient.Models;
using TarsClient.Services;

namespace TarsClient.ViewModels;

public enum LineKind { User, Tars, Meta, Error, System }

public sealed partial class TermLine(LineKind kind, string text) : ObservableObject
{
    public LineKind Kind { get; } = kind;
    [ObservableProperty] public partial string Text { get; set; } = text;
}

public sealed partial class TimerItem : ObservableObject
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Label { get; init; }
    public double Due { get; init; }
    [ObservableProperty] public partial string Display { get; set; } = "";
}

/// <summary>
/// The client's brain: connection state machine (drives the title-bar tag), protocol handling,
/// typewriter, timers/alarms, push-to-talk and the half-duplex mic sending rules.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    const int MaxLines = 200;

    public SettingsStore Store { get; }
    public ClientSettings S => Store.Current;
    public ServerConnection Server { get; }
    public AudioPlayback Playback { get; }
    public AudioCapture Capture { get; }
    public AudioDevices Devices { get; }
    public HotkeyService Hotkeys { get; }
    public LocalVoice Voice { get; }
    public GameMode Game { get; }
    public AdminApi Admin { get; }
    public SettingsViewModel Settings { get; }

    readonly Dispatcher _ui;
    readonly DispatcherTimer _typeTimer, _meterTimer, _timerTick, _flashTimer, _chimeTimer;
    readonly DispatcherTimer _personalityDebounce;

    // --- state read by the capture thread
    volatile bool _pttSending;
    volatile bool _handsFree;
    volatile bool _micMuted;
    volatile float _lastRms;
    int _pttGeneration;

    string _serverState = "idle";
    bool _pttHeld;
    bool _micBlocked;
    bool _dialedNote;
    string _replyTarget = "";
    int _replyShown;
    DateTime _typeLast;
    TermLine? _replyLine;
    double _clockOffset;
    int _chimes;

    public ObservableCollection<TermLine> Lines { get; } = [];
    public ObservableCollection<TimerItem> Timers { get; } = [];

    [ObservableProperty] public partial string AssistantName { get; set; } = "TARS";
    [ObservableProperty] public partial string StatusTag { get; set; } = "NO CARRIER";
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial string LastUtterance { get; set; } = "";
    [ObservableProperty] public partial string ReplyText { get; set; } = "";
    [ObservableProperty] public partial bool CursorVisible { get; set; }
    [ObservableProperty] public partial bool AlarmActive { get; set; }
    [ObservableProperty] public partial bool FlashOn { get; set; }
    [ObservableProperty] public partial double MicLevel { get; set; }
    [ObservableProperty] public partial double VolumeLevel { get; set; }
    [ObservableProperty] public partial bool VolumeHot { get; set; }
    [ObservableProperty] public partial string VolumeText { get; set; } = "";
    [ObservableProperty] public partial double BackgroundOpacity { get; set; } = 1;
    [ObservableProperty] public partial string ModeLabel { get; set; } = "WAKE";
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool SettingsOpen { get; set; }
    [ObservableProperty] public partial bool PromptActive { get; set; }
    [ObservableProperty] public partial string PromptText { get; set; } = "";
    [ObservableProperty] public partial bool Topmost { get; set; }
    [ObservableProperty] public partial bool Scanlines { get; set; }
    [ObservableProperty] public partial bool Glow { get; set; }
    [ObservableProperty] public partial double BaseFontSize { get; set; }
    [ObservableProperty] public partial bool HasTimers { get; set; }
    [ObservableProperty] public partial bool PttPressed { get; set; }
    [ObservableProperty] public partial string VoiceTag { get; set; } = "";

    /// <summary>The window is on screen: meters and animations only run then.</summary>
    public bool IsVisible { get; set; } = true;

    public event Action<bool>? ShowRequested;           // true = alarm (topmost + flash taskbar)
    public event Action? AlarmStopped;

    public MainViewModel(SettingsStore store)
    {
        Store = store;
        _ui = Dispatcher.CurrentDispatcher;

        Server = new ServerConnection(_ui, () => (S.ServerUrl, S.AccessKey));
        Admin = new AdminApi(() => (S.ServerUrl, S.AccessKey));
        Playback = new AudioPlayback(_ui) { Volume = S.Volume };
        Capture = new AudioCapture(_ui);
        Devices = new AudioDevices();
        Hotkeys = new HotkeyService(_ui) { Swallow = S.PttSwallow };
        Game = new GameMode(() => S.Tts.GameMode);
        Voice = new LocalVoice(_ui, () => S, Playback, Game);
        Settings = new SettingsViewModel(this);

        _typeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _typeTimer.Tick += (_, _) => TypeTick();
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _meterTimer.Tick += (_, _) => MicLevel = Meter(_lastRms);
        _timerTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timerTick.Tick += (_, _) => RenderTimers();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _flashTimer.Tick += (_, _) => FlashOn = !FlashOn;
        _chimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _chimeTimer.Tick += (_, _) => { if (++_chimes > 8) StopAlarm(false); else Playback.Chime(); };
        _personalityDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _personalityDebounce.Tick += (_, _) =>
        {
            _personalityDebounce.Stop();
            Server.SendJson(new { type = "settings", humor = Settings.Humor, honesty = Settings.Honesty, brevity = Settings.Brevity });
        };

        Topmost = S.Topmost;
        Scanlines = S.Scanlines;
        Glow = S.Glow;
        BaseFontSize = S.FontSize;
        BackgroundOpacity = S.BackgroundOpacity;
        IsExpanded = S.Layout == "expanded";
        _micMuted = S.MicMuted;
        ApplyMode(S.Mode, send: false);
        UpdateVolumeMeter();
    }

    // ================================================================== startup

    public async void Start()
    {
        Boot("TARS DESKTOP v1.0");
        Boot($"MEM {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 30} GB OK · AUDIO OK · VOICE LOCAL");

        Server.Connected += OnConnected;
        Server.Disconnected += _ => { IsConnected = false; RecomputeStatus(); };
        Server.RetryFailed += OnRetryFailed;
        Server.AccessDenied += () =>
        {
            IsConnected = false;
            Add(LineKind.Error, "ACCESS DENIED: add this PC to ALLOWED_CLIENTS or set an access key");
            ReplyText = "ACCESS DENIED: add this PC to ALLOWED_CLIENTS or set an access key";
            RecomputeStatus();
        };
        Server.Message += OnMessage;
        Server.Binary += _ => Log.Write("ws: ignored server WAV (voice is local)");

        Playback.PlaybackStarted += () => { Server.SendJson(new { type = "playback", state = "start" }); RecomputeStatus(); };
        Playback.PlaybackEnded += () => { Server.SendJson(new { type = "playback", state = "end" }); CursorVisible = _replyShown < _replyTarget.Length; RecomputeStatus(); };

        Capture.Frame += OnMicFrame;
        Capture.BlockedChanged += blocked =>
        {
            _micBlocked = blocked;
            if (blocked) Add(LineKind.Error, "MIC BLOCKED: Settings → Privacy → Microphone → Let desktop apps access your microphone");
            RecomputeStatus();
        };
        Devices.Changed += () => { Capture.Restart(); Playback.Reopen(); };

        Hotkeys.Configure(S.PttKey, S.StopHotkey);
        Hotkeys.PttDown += PttStart;
        Hotkeys.PttUp += () => _ = PttEnd();
        Hotkeys.StopPressed += StopAll;
        Hotkeys.Start();

        Voice.StateChanged += UpdateVoiceTag;
        Voice.Note += t => { if (S.ShowDetails || !t.StartsWith("  ·")) Add(LineKind.Meta, t.TrimStart()); };

        Playback.Open(S.OutputDevice);
        Capture.Start(S.InputDevice);
        Game.Start();
        Voice.Start();

        if (string.IsNullOrWhiteSpace(S.AccessKey))
        {
            var key = await Admin.FetchKeyFromSpecAsync();
            if (key != null)
            {
                S.AccessKey = key;
                Store.Save();
                Boot("ACCESS KEY PROVISIONED FROM SERVER · DPAPI SEALED");
            }
        }
        Server.Start();
    }

    void Boot(string text) => Add(LineKind.System, text);

    void OnConnected()
    {
        IsConnected = true;
        _serverState = "idle";
        Boot($"CONNECT {new Uri(S.ServerUrl).Authority} · TLS · CA PINNED");
        Server.SendJson(new { type = "client", name = "tars-desktop", tts = "local" });
        Server.SendJson(new { type = "mode", mode = S.Mode });
        RecomputeStatus();
    }

    void OnRetryFailed(int attempt)
    {
        if (attempt == 1 && !_dialedNote)
        {
            _dialedNote = true;
            Add(LineKind.System, $"ATDT {new Uri(S.ServerUrl).Host}…");
        }
        RecomputeStatus();
    }

    // ================================================================== protocol

    void OnMessage(string type, JsonElement m)
    {
        switch (type)
        {
            case "hello":
                AssistantName = (Str(m, "name") ?? "TARS").ToUpperInvariant();
                RecomputeStatus();
                break;
            case "settings":
                if (m.TryGetProperty("settings", out var st)) Settings.FromServer(st);
                break;
            case "timers":
                OnTimers(m);
                break;
            case "status":
                _serverState = Str(m, "state") ?? "idle";
                RecomputeStatus();
                break;
            case "heard":
                if (S.ShowDetails && m.TryGetProperty("accepted", out var acc) && !acc.GetBoolean())
                    Add(LineKind.Meta, $"  · ignored: \"{Str(m, "text")}\" ({Str(m, "reason")})");
                break;
            case "user":
                BeginTurn(Str(m, "text") ?? "");
                break;
            case "delta":
                AppendReply(Str(m, "text") ?? "", raw: true);
                break;
            case "reply":
                AppendReply(Str(m, "text") ?? "", raw: false);
                break;
            case "tool":
                if (S.ShowDetails) Add(LineKind.Meta, $"  · {Str(m, "name")}: {ToolArg(m)}");
                break;
            case "done":
                if (S.ShowDetails) Add(LineKind.Meta, "  · " + DoneLine(m));
                _serverState = "idle";
                _replyLine = null;
                RecomputeStatus();
                break;
            case "alarm":
                RingAlarm(Str(m, "text") ?? "Alarm");
                break;
            case "stop":
                StopSpeech();
                StopAlarm(false);
                break;
            case "error":
                Add(LineKind.Error, "ERR " + Str(m, "text"));
                break;
            case "client_ok":
                if (Str(m, "tts") != "local") Log.Write($"server acknowledged tts={Str(m, "tts")}");
                break;
            case "say":
                Voice.Say(Str(m, "speak") ?? Str(m, "text") ?? "", Str(m, "kind") ?? "reply");
                break;
        }
    }

    static string? Str(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    static string ToolArg(JsonElement m)
    {
        if (!m.TryGetProperty("args", out var a) || a.ValueKind != JsonValueKind.Object) return "";
        foreach (var k in new[] { "query", "command", "fact", "entity", "result" })
            if (a.TryGetProperty(k, out var v)) return v.ToString();
        return a.ToString();
    }

    static string DoneLine(JsonElement m)
    {
        var bits = new List<string> { Str(m, "via") ?? "" };
        if (m.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            if (t.TryGetProperty("first_audio_ms", out var fa) && fa.TryGetDouble(out var fam)) bits.Add($"voice {fam / 1000:0.0} s");
            if (t.TryGetProperty("total_ms", out var to) && to.TryGetDouble(out var tom)) bits.Add($"total {tom / 1000:0.0} s");
        }
        return string.Join(" · ", bits.Where(b => b != ""));
    }

    // ================================================================== turns + typewriter

    void BeginTurn(string text)
    {
        if (S.PopOnReply && !IsVisible) ShowRequested?.Invoke(false);
        FinishTyping();
        _hadTurn = true;
        LastUtterance = "> " + text;
        Add(LineKind.User, "> " + text);
        _replyTarget = "";
        _replyShown = 0;
        _replyLine = null;
        ReplyText = "";
    }

    void AppendReply(string text, bool raw)
    {
        if (text.Length == 0) return;
        // Never lag behind the audio: a new sentence finishes the current one instantly.
        FinishTyping();
        _replyTarget = raw || _replyTarget.Length == 0 ? _replyTarget + text : _replyTarget.TrimEnd() + " " + text.TrimStart();
        if (_replyLine == null)
        {
            _replyLine = new TermLine(LineKind.Tars, $"{AssistantName}: ");
            AddLine(_replyLine);
        }
        if (!IsVisible) { FinishTyping(); return; }
        CursorVisible = true;
        _typeLast = DateTime.UtcNow;
        _typeTimer.Start();
    }

    void TypeTick()
    {
        var now = DateTime.UtcNow;
        int add = (int)Math.Max(1, (now - _typeLast).TotalSeconds * Math.Max(10, S.TypewriterCps));
        _typeLast = now;
        _replyShown = Math.Min(_replyTarget.Length, _replyShown + add);
        RenderReply();
        if (_replyShown >= _replyTarget.Length)
        {
            _typeTimer.Stop();
            CursorVisible = Playback.IsPlaying;
        }
    }

    void FinishTyping()
    {
        _typeTimer.Stop();
        _replyShown = _replyTarget.Length;
        RenderReply();
    }

    void RenderReply()
    {
        var shown = _replyTarget[.._replyShown];
        ReplyText = shown.Length == 0 ? "" : $"{AssistantName}: {shown}";
        if (_replyLine != null) _replyLine.Text = $"{AssistantName}: {shown}";
    }

    // ================================================================== scrollback

    public void Add(LineKind kind, string text) => AddLine(new TermLine(kind, text));

    void AddLine(TermLine line)
    {
        Lines.Add(line);
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
    }

    // ================================================================== status

    void RecomputeStatus()
    {
        string tag;
        if (AlarmActive) tag = "ALARM";
        else if (!IsConnected) tag = Server.IsDenied ? "ACCESS DENIED" : "NO CARRIER";
        else if (Playback.IsPlaying) tag = "SPEAKING";
        else if (_pttHeld) tag = "HEARING";
        else if (_serverState is "hearing" or "transcribing" or "thinking") tag = _serverState.ToUpperInvariant();
        else if (_micBlocked) tag = "MIC BLOCKED";
        else if (Voice.State == VoiceState.Warming && _serverState != "idle") tag = "VOICE: WARMING";
        else tag = S.Mode == "ptt" ? "STANDBY" : "LISTENING";
        StatusTag = tag;
        if (!Playback.IsPlaying && _replyShown >= _replyTarget.Length) CursorVisible = false;
    }

    void UpdateVoiceTag()
    {
        VoiceTag = Voice.State switch
        {
            VoiceState.Online => "",
            VoiceState.Warming => "VOICE: WARMING",
            VoiceState.GameMode => "VOICE: GAME",
            VoiceState.NotInstalled => "VOICE: NOT INSTALLED",
            VoiceState.Starting => "VOICE: BOOTING",
            _ => "VOICE: OFFLINE",
        };
        Settings.RefreshVoice();
        RecomputeStatus();
    }

    // ================================================================== mic + push-to-talk

    void OnMicFrame(byte[] pcm, float rms)
    {
        _lastRms = rms;
        if (_micMuted) return;
        bool send = _pttSending;
        if (!send && _handsFree)
        {
            // Half duplex: never while TARS (or the chime) is audible, nor for the echo tail after.
            var sinceAudio = (DateTime.UtcNow.Ticks - Playback.LastAudioTicks) / TimeSpan.TicksPerMillisecond;
            send = !Playback.IsPlaying && sinceAudio > S.EchoTailMs + 60;   // + output latency
        }
        if (send) Server.SendAudio(pcm);
    }

    [RelayCommand]
    public void PttStart()
    {
        if (_pttHeld) return;
        _pttHeld = true;
        PttPressed = true;
        _pttGeneration++;
        StopSpeech();
        if (AlarmActive) StopAlarm(true);
        Server.SendJson(new { type = "ptt", state = "start" });
        _pttSending = true;
        RecomputeStatus();
    }

    [RelayCommand]
    public async Task PttEnd()
    {
        if (!_pttHeld) return;
        _pttHeld = false;
        PttPressed = false;
        int gen = _pttGeneration;
        // Keep streaming 300 ms so the last word isn't clipped.
        await Task.Delay(300);
        if (gen != _pttGeneration) return;
        _pttSending = false;
        Server.SendJson(new { type = "ptt", state = "end" });
        RecomputeStatus();
    }

    [RelayCommand]
    void CycleMode() => ApplyMode(S.Mode switch { "ptt" => "wake", "wake" => "open", _ => "ptt" }, send: true);

    bool _hadTurn;

    string IdleHint => S.Mode switch
    {
        "ptt" => $"hold {S.PttKey.ToUpperInvariant()} to talk · or just type",
        "wake" => $"say \"{AssistantName}\" · or just type",
        _ => "listening · or just type",
    };

    public void ApplyMode(string mode, bool send)
    {
        S.Mode = mode;
        _handsFree = mode != "ptt";
        ModeLabel = mode.ToUpperInvariant();
        if (!_hadTurn) LastUtterance = IdleHint;
        if (send)
        {
            Store.Save();
            Server.SendJson(new { type = "mode", mode });
        }
        RecomputeStatus();
        OnPropertyChanged(nameof(Mode));
    }

    public string Mode => S.Mode;

    public bool MicMuted
    {
        get => _micMuted;
        set
        {
            _micMuted = value;
            S.MicMuted = value;
            Store.Save();
            OnPropertyChanged();
        }
    }

    static double Meter(float rms)
    {
        // -54 dBFS … -6 dBFS across 8 cells
        double db = 20 * Math.Log10(Math.Max(rms, 1e-6));
        return Math.Clamp(Math.Round((db + 54) / 6), 0, 8);
    }

    // ================================================================== text prompt

    [RelayCommand]
    void SendPrompt()
    {
        var text = PromptText.Trim();
        PromptText = "";
        PromptActive = IsExpanded;
        if (text.Length == 0) return;
        if (!IsConnected) { Add(LineKind.Error, "NO CARRIER: not sent"); return; }
        StopSpeech();
        Server.SendJson(new { type = "text", text, speak = S.SpeakTyped });
    }

    // ================================================================== volume

    public double VolumePercent
    {
        get => Math.Round(S.Volume * 100);
        set
        {
            S.Volume = Math.Clamp(Math.Round(value / 5) * 5, 0, 150) / 100.0;
            Playback.Volume = S.Volume;
            Store.Save();
            UpdateVolumeMeter();
            OnPropertyChanged();
        }
    }

    public void NudgeVolume(int steps) => VolumePercent += steps * 5;

    void UpdateVolumeMeter()
    {
        VolumeLevel = Math.Clamp(Math.Round(S.Volume * 9), 0, 9);
        VolumeHot = S.Volume > 1.0;
        VolumeText = $"{S.Volume * 100:0}%";
    }

    // ================================================================== timers + alarms

    void OnTimers(JsonElement m)
    {
        double now = m.TryGetProperty("now", out var n) && n.TryGetDouble(out var nv) ? nv : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        _clockOffset = now - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        Timers.Clear();
        if (m.TryGetProperty("timers", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var t in arr.EnumerateArray())
                Timers.Add(new TimerItem
                {
                    Id = Str(t, "id") ?? "",
                    Kind = Str(t, "kind") ?? "timer",
                    Label = Str(t, "label") ?? "",
                    Due = t.TryGetProperty("due", out var d) && d.TryGetDouble(out var dv) ? dv : 0,
                });
        HasTimers = Timers.Count > 0;
        RenderTimers();
        if (HasTimers) _timerTick.Start(); else _timerTick.Stop();
    }

    void RenderTimers()
    {
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + _clockOffset;
        foreach (var t in Timers)
        {
            if (t.Kind == "timer")
            {
                var left = TimeSpan.FromSeconds(Math.Max(0, Math.Round(t.Due - now)));
                var label = (t.Label is "" or "timer" ? "TIMER" : t.Label).ToUpperInvariant();
                t.Display = left.TotalHours >= 1 ? $"{label} {(int)left.TotalHours}:{left:mm\\:ss}" : $"{label} {(int)left.TotalMinutes:00}:{left:ss}";
            }
            else
            {
                // The server's clock is authoritative; show the reminder time in local time.
                var at = DateTimeOffset.FromUnixTimeMilliseconds((long)((t.Due - _clockOffset) * 1000)).ToLocalTime();
                t.Display = $"{at:HH:mm} {t.Label}";
            }
        }
    }

    [RelayCommand]
    void CancelTimer(string id) => Server.SendJson(new { type = "cancel_timer", id });

    void RingAlarm(string text)
    {
        AlarmActive = true;
        FinishTyping();
        ReplyText = "⏰ " + text;
        Add(LineKind.Tars, "⏰ " + text);
        _chimes = 0;
        Playback.Chime();
        _chimeTimer.Stop();
        _chimeTimer.Start();
        _flashTimer.Start();
        ShowRequested?.Invoke(true);
        RecomputeStatus();
    }

    /// <summary>Esc, click, stop hotkey: dismiss everywhere (the server then stops every client).</summary>
    [RelayCommand]
    public void Dismiss()
    {
        StopSpeech();
        StopAlarm(true);
    }

    public void StopAll() => Dismiss();

    void StopAlarm(bool tellOthers)
    {
        if (!AlarmActive) return;
        AlarmActive = false;
        _chimeTimer.Stop();
        _flashTimer.Stop();
        FlashOn = false;
        if (tellOthers) Server.SendJson(new { type = "dismiss" });
        AlarmStopped?.Invoke();
        RecomputeStatus();
    }

    void StopSpeech()
    {
        Voice.Stop();
        Playback.Stop();
        FinishTyping();
    }

    // ================================================================== personality (sent live)

    public void PersonalityChanged() { _personalityDebounce.Stop(); _personalityDebounce.Start(); }

    // ================================================================== window-level toggles

    [RelayCommand]
    void TogglePin() => Topmost = !Topmost;

    partial void OnTopmostChanged(bool value)
    {
        if (S.Topmost == value) return;
        S.Topmost = value;
        Store.Save();
    }

    [RelayCommand]
    void ToggleSettings() => SettingsOpen = !SettingsOpen;

    partial void OnSettingsOpenChanged(bool value)
    {
        if (value) Settings.OnOpened(); else Settings.OnClosed();
    }

    public void Dispatch(Action a) => _ui.BeginInvoke(a);

    public void SetVisible(bool visible)
    {
        IsVisible = visible;
        if (visible) { _meterTimer.Start(); RenderTimers(); }
        else { _meterTimer.Stop(); FinishTyping(); }
    }

    public void ApplyAppearance()
    {
        Scanlines = S.Scanlines;
        Glow = S.Glow;
        BaseFontSize = S.FontSize;
        BackgroundOpacity = S.BackgroundOpacity;
        AppearanceChanged?.Invoke();
    }

    public event Action? AppearanceChanged;

    [RelayCommand]
    void ClearScrollback()
    {
        Lines.Clear();
        _replyLine = null;
    }

    public void ReconfigureHotkeys()
    {
        Hotkeys.Swallow = S.PttSwallow;
        Hotkeys.Configure(S.PttKey, S.StopHotkey);
    }

    public void Shutdown()
    {
        Store.SaveNow();
        Hotkeys.Dispose();
        Capture.Dispose();
        Voice.Dispose();
        Playback.Dispose();
        Server.Dispose();
        Devices.Dispose();
        Game.Dispose();
    }
}
