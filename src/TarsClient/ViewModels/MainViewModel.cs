namespace TarsClient.ViewModels;

/// <summary>
/// The client's brain: the connection state machine (it drives the title-bar tag), protocol handling, the typewriter,
/// timers and alarms, push-to-talk and the half-duplex mic rules. Split by concern across the partial files.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    #region Fields

    private const int MaxLines = 200;

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _typeTimer;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _timerTick;
    private readonly DispatcherTimer _flashTimer;
    private readonly DispatcherTimer _chimeTimer;
    private readonly DispatcherTimer _personalityDebounce;

    private string _serverState = "idle";
    private bool _micBlocked;
    private bool _dialedNote;
    private string[] _serverFeatures = [];

    #endregion

    #region Constructor

    /// <summary>Creates the services from the saved settings; nothing runs until <see cref="Start"/>.</summary>
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
        Stt = new SttClient(_ui, () => S.Tts.SidecarUrl, SttConfig);
        Settings = new SettingsViewModel(this);

        _typeTimer = NewTimer(TimeSpan.FromMilliseconds(33), TypeTick, DispatcherPriority.Render);
        _meterTimer = NewTimer(TimeSpan.FromMilliseconds(80), () => MicLevel = Meter(_lastRms));
        _timerTick = NewTimer(TimeSpan.FromSeconds(1), RenderTimers);
        _flashTimer = NewTimer(TimeSpan.FromMilliseconds(250), () => FlashOn = !FlashOn);
        _chimeTimer = NewTimer(TimeSpan.FromSeconds(10), ChimeAgain);
        _personalityDebounce = NewTimer(TimeSpan.FromMilliseconds(300), SendPersonality);

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

    #endregion

    #region Events

    /// <summary>Bring the window up; <c>true</c> means an alarm (topmost and flash the taskbar).</summary>
    public event Action<bool>? ShowRequested;

    /// <summary>A ringing alarm was dismissed.</summary>
    public event Action? AlarmStopped;

    /// <summary>Scanlines, glow, font size or opacity changed.</summary>
    public event Action? AppearanceChanged;

    #endregion

    #region Properties

    /// <summary>Where the settings live.</summary>
    public SettingsStore Store { get; }

    /// <summary>The current settings.</summary>
    public ClientSettings S => Store.Current;

    /// <summary>The WebSocket to the TARS server.</summary>
    public ServerConnection Server { get; }

    /// <summary>Speaker output.</summary>
    public AudioPlayback Playback { get; }

    /// <summary>Microphone input.</summary>
    public AudioCapture Capture { get; }

    /// <summary>Audio device change notifications.</summary>
    public AudioDevices Devices { get; }

    /// <summary>Push-to-talk and stop hotkeys.</summary>
    public HotkeyService Hotkeys { get; }

    /// <summary>The local voice sidecar.</summary>
    public LocalVoice Voice { get; }

    /// <summary>Local speech-to-text over the sidecar.</summary>
    public SttClient Stt { get; }

    /// <summary>Game-mode detection.</summary>
    public GameMode Game { get; }

    /// <summary>The server's admin API.</summary>
    public AdminApi Admin { get; }

    /// <summary>The settings screen.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>The scrollback.</summary>
    public ObservableCollection<TermLine> Lines { get; } = [];

    /// <summary>Running timers and reminders.</summary>
    public ObservableCollection<TimerItem> Timers { get; } = [];

    /// <summary>The window is on screen: meters and animations only run then.</summary>
    public bool IsVisible { get; private set; } = true;

    /// <summary>The assistant's name from the server's <c>hello</c>.</summary>
    [ObservableProperty]
    public partial string AssistantName { get; set; } = "TARS";

    /// <summary>The title-bar state tag.</summary>
    [ObservableProperty]
    public partial string StatusTag { get; set; } = "NO CARRIER";

    /// <summary>The server connection is up.</summary>
    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    /// <summary>The last thing you said, or a hint before the first turn.</summary>
    [ObservableProperty]
    public partial string LastUtterance { get; set; } = "";

    /// <summary>The reply as typed out so far.</summary>
    [ObservableProperty]
    public partial string ReplyText { get; set; } = "";

    /// <summary>The block cursor after the reply.</summary>
    [ObservableProperty]
    public partial bool CursorVisible { get; set; }

    /// <summary>An alarm is ringing.</summary>
    [ObservableProperty]
    public partial bool AlarmActive { get; set; }

    /// <summary>The alarm flash phase.</summary>
    [ObservableProperty]
    public partial bool FlashOn { get; set; }

    /// <summary>Mic meter cells lit (0 to 8).</summary>
    [ObservableProperty]
    public partial double MicLevel { get; set; }

    /// <summary>Volume meter cells lit (0 to 9).</summary>
    [ObservableProperty]
    public partial double VolumeLevel { get; set; }

    /// <summary>Volume is above 100 %.</summary>
    [ObservableProperty]
    public partial bool VolumeHot { get; set; }

    /// <summary>Volume as text.</summary>
    [ObservableProperty]
    public partial string VolumeText { get; set; } = "";

    /// <summary>Window background opacity.</summary>
    [ObservableProperty]
    public partial double BackgroundOpacity { get; set; } = 1;

    /// <summary>The listening mode in capitals.</summary>
    [ObservableProperty]
    public partial string ModeLabel { get; set; } = "WAKE";

    /// <summary>The window is in its large layout.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>The settings screen is showing.</summary>
    [ObservableProperty]
    public partial bool SettingsOpen { get; set; }

    /// <summary>The text prompt is showing.</summary>
    [ObservableProperty]
    public partial bool PromptActive { get; set; }

    /// <summary>The text prompt's contents.</summary>
    [ObservableProperty]
    public partial string PromptText { get; set; } = "";

    /// <summary>Always on top.</summary>
    [ObservableProperty]
    public partial bool Topmost { get; set; }

    /// <summary>CRT scanlines overlay.</summary>
    [ObservableProperty]
    public partial bool Scanlines { get; set; }

    /// <summary>Phosphor glow on text.</summary>
    [ObservableProperty]
    public partial bool Glow { get; set; }

    /// <summary>The terminal font size.</summary>
    [ObservableProperty]
    public partial double BaseFontSize { get; set; }

    /// <summary>There are timers to show.</summary>
    [ObservableProperty]
    public partial bool HasTimers { get; set; }

    /// <summary>Push-to-talk is held.</summary>
    [ObservableProperty]
    public partial bool PttPressed { get; set; }

    /// <summary>The voice's state when it isn't simply online.</summary>
    [ObservableProperty]
    public partial string VoiceTag { get; set; } = "";

    #endregion

    #region Public Methods

    /// <summary>Wires the services together, provisions the access key and connects.</summary>
    public async void Start()
    {
        Boot("TARS DESKTOP v1.0");
        Boot($"MEM {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 30} GB OK · AUDIO OK · VOICE LOCAL");

        WireServer();
        WirePlayback();
        WireStt();
        WireCapture();
        WireHotkeys();
        Voice.StateChanged += UpdateVoiceTag;
        Voice.Note += OnVoiceNote;

        Playback.Open(S.OutputDevice);
        Capture.Start(S.InputDevice);
        Game.Start();
        Voice.Start();
        Stt.Start();

        await EnsureAccessKeyAsync();
        Server.Start();
        _ = LoadWakeWordsAsync();
    }

    /// <summary>Adds a scrollback line.</summary>
    public void Add(LineKind kind, string text) => AddLine(new TermLine(kind, text));

    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    public void Dispatch(Action action) => _ui.BeginInvoke(action);

    /// <summary>The window was shown or hidden: meters, timers and the typewriter only run while it's visible.</summary>
    public void SetVisible(bool visible)
    {
        IsVisible = visible;
        if (visible)
        {
            _meterTimer.Start();
            RenderTimers();
            return;
        }
        _meterTimer.Stop();
        FinishTyping();
    }

    /// <summary>Pushes the appearance settings to the window.</summary>
    public void ApplyAppearance()
    {
        Scanlines = S.Scanlines;
        Glow = S.Glow;
        BaseFontSize = S.FontSize;
        BackgroundOpacity = S.BackgroundOpacity;
        AppearanceChanged?.Invoke();
    }

    /// <summary>Re-reads the hotkey settings.</summary>
    public void ReconfigureHotkeys()
    {
        Hotkeys.Swallow = S.PttSwallow;
        Hotkeys.Configure(S.PttKey, S.StopHotkey);
    }

    /// <summary>A personality slider moved: send it once the slider settles.</summary>
    public void PersonalityChanged()
    {
        _personalityDebounce.Stop();
        _personalityDebounce.Start();
    }

    /// <summary>Saves and releases everything on exit.</summary>
    public void Shutdown()
    {
        Stt.Dispose();
        Store.SaveNow();
        Hotkeys.Dispose();
        Capture.Dispose();
        Voice.Dispose();
        Playback.Dispose();
        Server.Dispose();
        Devices.Dispose();
        Game.Dispose();
    }

    #endregion

    #region Commands

    [RelayCommand]
    private void TogglePin() => Topmost = !Topmost;

    [RelayCommand]
    private void ToggleSettings() => SettingsOpen = !SettingsOpen;

    [RelayCommand]
    private void ClearScrollback()
    {
        Lines.Clear();
        _replyLine = null;
    }

    #endregion

    #region Private Methods

    private static DispatcherTimer NewTimer(TimeSpan interval, Action tick, DispatcherPriority priority = DispatcherPriority.Background)
    {
        var timer = new DispatcherTimer(priority) { Interval = interval };
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private (string model, string device, string speaker, double threshold) SttConfig()
    {
        bool locked = S.Stt.VoiceLock && File.Exists(S.Stt.Voiceprint);
        return Game.Active
            ? (S.Stt.GameModel, "cpu", locked ? S.Stt.Voiceprint : "", S.Stt.SpeakerThreshold)
            : (S.Stt.Model, "cuda", locked ? S.Stt.Voiceprint : "", S.Stt.SpeakerThreshold);
    }

    private void WireServer()
    {
        Server.Connected += OnConnected;
        Server.Disconnected += _ =>
        {
            IsConnected = false;
            RecomputeStatus();
        };
        Server.RetryFailed += OnRetryFailed;
        Server.AccessDenied += OnAccessDenied;
        Server.Message += OnMessage;
        Server.Binary += _ =>
        {
            if (!_dropTurnAudio) Log.Write("ws: ignored server WAV (voice is local)");
        };
    }

    private void WirePlayback()
    {
        Playback.PlaybackStarted += () =>
        {
            Server.SendJson(ClientMessages.Playback(start: true));
            if (_sttLocal && !_pttSending) Stt.Reset();   // never transcribe TARS's own voice
            RecomputeStatus();
        };
        Playback.PlaybackEnded += () =>
        {
            _lastPlaybackEnd = DateTime.UtcNow;
            Server.SendJson(ClientMessages.Playback(start: false));
            CursorVisible = _replyShown < _replyTarget.Length;
            RecomputeStatus();
        };
    }

    private void WireStt()
    {
        Stt.ReadyChanged += _ => UpdateSttRoute();
        Stt.SpeechChanged += speech =>
        {
            if (speech) _localState = "hearing";
            RecomputeStatus();
        };
        Stt.Transcribing += () =>
        {
            _localState = "transcribing";
            RecomputeStatus();
        };
        Stt.UtteranceReady += OnUtterance;
        Stt.Rejected += (reason, text) =>
        {
            _localState = "";
            if (S.ShowDetails) Add(LineKind.Meta, $"  · ignored: \"{text}\" ({reason})");
            RecomputeStatus();
        };
        Game.Changed += Stt.Reconfigure;
    }

    private void WireCapture()
    {
        Capture.Frame += OnMicFrame;
        Capture.BlockedChanged += blocked =>
        {
            _micBlocked = blocked;
            if (blocked) Add(LineKind.Error, "MIC BLOCKED: Settings → Privacy → Microphone → Let desktop apps access your microphone");
            RecomputeStatus();
        };
        Capture.EndpointMutedChanged += muted =>
        {
            if (muted) Add(LineKind.Error, $"MIC MUTED IN WINDOWS: {Capture.DeviceName} delivers silence. Check the mic's mute button, or click MIC to unmute");
            else Add(LineKind.System, "MIC UNMUTED");
            RecomputeStatus();
        };
        Devices.Changed += () =>
        {
            Capture.Restart();
            Playback.Reopen();
        };
    }

    private void WireHotkeys()
    {
        Hotkeys.Configure(S.PttKey, S.StopHotkey);
        Hotkeys.PttDown += PttStart;
        Hotkeys.PttUp += () => _ = PttEnd();
        Hotkeys.StopPressed += StopAll;
        Hotkeys.Start();
    }

    private void OnVoiceNote(string text)
    {
        // Lines starting "  ·" are details; everything else always shows.
        if (S.ShowDetails || !text.StartsWith("  ·", StringComparison.Ordinal)) Add(LineKind.Meta, text.TrimStart());
    }

    /// <summary>Provisions the access key from the server's spec (allowlisted PCs only), and heals it after a rotation.</summary>
    private async Task EnsureAccessKeyAsync()
    {
        if (!string.IsNullOrWhiteSpace(S.AccessKey) && await Admin.KeyWorksAsync()) return;
        var key = await Admin.FetchKeyFromSpecAsync();
        if (key == null)
        {
            Add(LineKind.Error, "NO ACCESS KEY: set it under SET → SYSTEM");
            return;
        }
        if (key == S.AccessKey) return;
        S.AccessKey = key;
        Store.Save();
        Boot("ACCESS KEY PROVISIONED FROM SERVER · DPAPI SEALED");
    }

    private void Boot(string text) => Add(LineKind.System, text);

    private void OnConnected()
    {
        IsConnected = true;
        _serverState = "idle";
        _serverStt = false;
        Boot($"CONNECT {new Uri(S.ServerUrl).Authority} · TLS · CA PINNED");
        SendClientHello();
        Server.SendJson(ClientMessages.Mode(S.Mode));
        RecomputeStatus();
    }

    private void OnRetryFailed(int attempt)
    {
        if (attempt == 1 && !_dialedNote)
        {
            _dialedNote = true;
            Add(LineKind.System, $"ATDT {new Uri(S.ServerUrl).Host}…");
        }
        RecomputeStatus();
    }

    private void OnAccessDenied()
    {
        const string denied = "ACCESS DENIED: add this PC to ALLOWED_CLIENTS or set an access key";
        IsConnected = false;
        Add(LineKind.Error, denied);
        ReplyText = denied;
        RecomputeStatus();
    }

    private void SendPersonality()
    {
        _personalityDebounce.Stop();
        Server.SendJson(ClientMessages.Personality(Settings.Humor, Settings.Honesty, Settings.Brevity));
    }

    partial void OnTopmostChanged(bool value)
    {
        if (S.Topmost == value) return;
        S.Topmost = value;
        Store.Save();
    }

    partial void OnSettingsOpenChanged(bool value)
    {
        if (value) Settings.OnOpened();
        else Settings.OnClosed();
    }

    #endregion
}
