namespace TarsClient.ViewModels;

/// <summary>The mic: push-to-talk, listening modes, half-duplex sending, local STT routing and stop semantics.</summary>
public sealed partial class MainViewModel
{
    #region Fields

    // Read by the capture thread.
    private volatile bool _pttSending;
    private volatile bool _handsFree;
    private volatile bool _micMuted;
    private volatile bool _sttLocal;
    private volatile float _lastRms;

    private bool _serverStt;                 // the server accepted {stt:"local"} and judges `utterance` itself
    private string _localState = "";         // hearing / transcribing, from the local VAD
    private DateTime _lastPlaybackEnd = DateTime.MinValue;
    private string[] _wakeWords = ["tars"];
    private bool _pttHeld;
    private int _pttGeneration;

    // Stop semantics (spec section 4): after a stop, audio already in flight for that turn is dropped until the
    // turn's `done` or the next `user`. Alarms are never dropped.
    private bool _dropTurnAudio;
    private bool _turnActive;

    #endregion

    #region Properties

    /// <summary>The listening mode: <c>ptt</c>, <c>wake</c> or <c>open</c>.</summary>
    public string Mode => S.Mode;

    /// <summary>Mic frames go to the local STT rather than the server.</summary>
    public bool SttLocal => _sttLocal;

    /// <summary>The server judges our <c>utterance</c> messages (wake name, follow-up window) itself.</summary>
    public bool ServerUnderstandsUtterances => _serverStt;

    /// <summary>Our own mute (the MIC toggle), separate from Windows' endpoint mute.</summary>
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

    private string IdleHint => S.Mode switch
    {
        "ptt" => $"hold {S.PttKey.ToUpperInvariant()} to talk · or just type",
        "wake" => $"say \"{AssistantName}\" · or just type",
        _ => "listening · or just type",
    };

    #endregion

    #region Public Methods

    /// <summary>Routes the mic to the local STT when it's wanted and up; otherwise to the server.</summary>
    public void UpdateSttRoute()
    {
        bool local = S.Stt.Mode == "local" && Stt.IsReady;
        if (local != _sttLocal)
        {
            _sttLocal = local;
            Add(LineKind.System, local ? "SPEECH RECOGNITION: LOCAL" : "SPEECH RECOGNITION: SERVER");
            if (IsConnected) SendClientHello();
        }
        if (local) Stt.Reconfigure();
        Settings.RefreshStt();
    }

    /// <summary>Switches listening mode, and tells the server when <paramref name="send"/> is set.</summary>
    public void ApplyMode(string mode, bool send)
    {
        S.Mode = mode;
        _handsFree = mode != "ptt";
        ModeLabel = mode.ToUpperInvariant();
        if (!_hadTurn) LastUtterance = IdleHint;
        if (send)
        {
            Store.Save();
            Server.SendJson(ClientMessages.Mode(mode));
        }
        RecomputeStatus();
        OnPropertyChanged(nameof(Mode));
    }

    /// <summary>A click on the MIC meter: offers to unmute a mic that Windows has muted.</summary>
    public async Task MicClicked()
    {
        if (!Capture.IsEndpointMuted) return;
        if (!IsExpanded && !SettingsOpen)
        {
            Capture.UnmuteEndpoint();
            return;
        }
        if (await Settings.ConfirmAsync($"Unmute {Capture.DeviceName} in Windows?")) Capture.UnmuteEndpoint();
    }

    /// <summary>The stop hotkey: silence and dismiss everywhere.</summary>
    public void StopAll() => Dismiss();

    /// <summary>A request that starts audio without a <c>user</c> event (<c>test_voice</c>): stop dropping.</summary>
    public void ExpectAudio() => _dropTurnAudio = false;

    #endregion

    #region Commands

    /// <summary>Push-to-talk pressed: interrupts TARS and starts a bracket.</summary>
    [RelayCommand]
    public void PttStart()
    {
        if (_pttHeld) return;
        _pttHeld = true;
        PttPressed = true;
        _pttGeneration++;
        StopSpeech();
        if (AlarmActive) StopAlarm(tellOthers: true);
        // With local STT the bracket lives in the sidecar; the server gets the finished `utterance` (source "ptt").
        if (_sttLocal) Stt.PttStart();
        else Server.SendJson(ClientMessages.Ptt(start: true));
        _pttSending = true;
        RecomputeStatus();
    }

    /// <summary>Push-to-talk released: keeps streaming 300 ms so the last word isn't clipped, then closes the bracket.</summary>
    [RelayCommand]
    public async Task PttEnd()
    {
        if (!_pttHeld) return;
        _pttHeld = false;
        PttPressed = false;
        int generation = _pttGeneration;
        await Task.Delay(300);
        if (generation != _pttGeneration) return;
        _pttSending = false;
        if (_sttLocal) Stt.PttEnd();
        else Server.SendJson(ClientMessages.Ptt(start: false));
        RecomputeStatus();
    }

    [RelayCommand]
    private void CycleMode() => ApplyMode(S.Mode switch { "ptt" => "wake", "wake" => "open", _ => "ptt" }, send: true);

    #endregion

    #region Private Methods

    /// <summary>Capture thread: decides whether this frame goes anywhere.</summary>
    private void OnMicFrame(byte[] pcm, float rms)
    {
        _lastRms = rms;
        if (_micMuted) return;
        if (!_pttSending && !(_handsFree && EchoClear())) return;
        if (_sttLocal) Stt.SendAudio(pcm);
        else Server.SendAudio(pcm);
    }

    /// <summary>Half duplex: never while TARS (or the chime) is audible, nor during the echo tail after.</summary>
    private bool EchoClear()
    {
        var sinceAudioMs = (DateTime.UtcNow.Ticks - Playback.LastAudioTicks) / TimeSpan.TicksPerMillisecond;
        return !Playback.IsPlaying && sinceAudioMs > S.EchoTailMs + 60;   // + output latency
    }

    private void SendClientHello() => Server.SendJson(ClientMessages.Client(S.Stt.Mode == "local" && Stt.IsReady));

    private void OnUtterance(Utterance u)
    {
        _localState = "";
        string source = u.Source == "ptt" ? "ptt" : S.Mode;
        if (S.ShowDetails) Add(LineKind.Meta, $"  · heard ({u.Model}, {u.SttMs} ms): {u.Text}");
        if (_serverStt) Server.SendJson(ClientMessages.Utterance(u, source));
        else SendIfAccepted(u, source);
        RecomputeStatus();
    }

    /// <summary>
    /// Interim gating for a server that can't take <c>utterance</c>: PTT and OPEN always pass; WAKE needs the name, or
    /// speech that started within 8 s of TARS finishing.
    /// </summary>
    private void SendIfAccepted(Utterance u, string source)
    {
        var startedAt = DateTime.UtcNow - TimeSpan.FromMilliseconds(u.DurationMs + u.SttMs);
        bool followUp = (startedAt - _lastPlaybackEnd).TotalSeconds < 8;
        bool accept = source is "ptt" or "open" || followUp || WakeWords.Mentions(u.Text, _wakeWords);
        if (!accept)
        {
            if (S.ShowDetails) Add(LineKind.Meta, $"  · ignored: \"{u.Text}\" (no wake name)");
            return;
        }
        if (!IsConnected)
        {
            Add(LineKind.Error, "NO CARRIER: not sent");
            return;
        }
        Server.SendJson(ClientMessages.Text(u.Text, speak: true));
    }

    private async Task LoadWakeWordsAsync()
    {
        try
        {
            var config = await Admin.GetConfigAsync();
            var wake = config.Fields.FirstOrDefault(f => f.Key == "WAKE_WORDS")?.Value;
            var name = config.Fields.FirstOrDefault(f => f.Key == "ASSISTANT_NAME")?.Value;
            _wakeWords = WakeWords.Parse(wake, name);
        }
        catch (Exception ex) when (ex is AdminException or HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Write($"wake words: {ex.Message} (using \"tars\")");
        }
    }

    /// <summary>
    /// Silence now. A user stop (Esc, the stop hotkey, PTT, a new message) also tells the server to cancel the running
    /// turn if it supports that; either way, chunks of the stopped turn still in flight are dropped.
    /// </summary>
    private void StopSpeech(bool fromServer = false)
    {
        bool turnInProgress = _turnActive || Playback.IsPlaying || _serverState is "thinking" or "transcribing";
        Voice.Stop();
        Playback.Stop();                       // sends `playback end` if anything was playing
        FinishTyping();
        if (!turnInProgress) return;
        _dropTurnAudio = true;
        if (!fromServer && _serverFeatures.Contains("stop")) Server.SendJson(ClientMessages.Stop());
    }

    private static double Meter(float rms)
    {
        // -54 dBFS … -6 dBFS across 8 cells
        double db = 20 * Math.Log10(Math.Max(rms, 1e-6));
        return Math.Clamp(Math.Round((db + 54) / 6), 0, 8);
    }

    #endregion
}
