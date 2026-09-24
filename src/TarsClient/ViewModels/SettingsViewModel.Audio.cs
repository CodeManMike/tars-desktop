using NAudio.CoreAudioApi;

namespace TarsClient.ViewModels;

/// <summary>AUDIO tab: devices, speech recognition, echo tail and hotkeys.</summary>
public sealed partial class SettingsViewModel
{
    #region Fields

    /// <summary>Where speech recognition runs.</summary>
    public static readonly string[] SttModes = ["LOCAL (this PC)", "SERVER"];

    /// <summary>The local Whisper models.</summary>
    public static readonly string[] SttModels = ["large-v3-turbo", "small.en"];

    #endregion

    #region Properties

    /// <summary>Capture devices.</summary>
    public ObservableCollection<AudioDevice> Inputs { get; } = [];

    /// <summary>Render devices.</summary>
    public ObservableCollection<AudioDevice> Outputs { get; } = [];

    /// <summary>The selected microphone; a change restarts capture on it.</summary>
    public int InputIndex
    {
        get => Math.Max(0, Inputs.ToList().FindIndex(d => d.Id == S.InputDevice));
        set
        {
            if (Inputs.Count == 0) return;
            var device = Inputs[(value + Inputs.Count) % Inputs.Count];
            S.InputDevice = device.Id;
            Save();
            _main.Capture.Start(device.Id);
        }
    }

    /// <summary>The selected speakers; a change reopens playback on them.</summary>
    public int OutputIndex
    {
        get => Math.Max(0, Outputs.ToList().FindIndex(d => d.Id == S.OutputDevice));
        set
        {
            if (Outputs.Count == 0) return;
            var device = Outputs[(value + Outputs.Count) % Outputs.Count];
            S.OutputDevice = device.Id;
            Save();
            _main.Playback.Open(device.Id);
        }
    }

    /// <summary>Local or server speech recognition.</summary>
    public int SttModeIndex
    {
        get => S.Stt.Mode == "server" ? 1 : 0;
        set
        {
            S.Stt.Mode = Wrap(value, SttModes.Length) == 1 ? "server" : "local";
            Save();
            _main.UpdateSttRoute();
        }
    }

    /// <summary>The local Whisper model (outside game mode).</summary>
    public int SttModelIndex
    {
        get => Math.Max(0, Array.IndexOf(SttModels, S.Stt.Model));
        set
        {
            S.Stt.Model = SttModels[Wrap(value, SttModels.Length)];
            Save();
            _main.Stt.Reconfigure();
        }
    }

    /// <summary>Where recognition runs right now, and who gates it.</summary>
    [ObservableProperty]
    public partial string SttStatus { get; set; } = "";

    /// <summary>How long after TARS stops before the mic opens again, in ms.</summary>
    public double EchoTail { get => S.EchoTailMs; set { S.EchoTailMs = (int)value; Save(); } }

    /// <summary>The push-to-talk key's name.</summary>
    [ObservableProperty]
    public partial string PttKey { get; set; } = "";

    /// <summary>The stop hotkey's name.</summary>
    [ObservableProperty]
    public partial string StopHotkey { get; set; } = "";

    /// <summary>Waiting for a key press to bind.</summary>
    [ObservableProperty]
    public partial bool Capturing { get; set; }

    /// <summary>Push-to-talk presses don't reach other apps.</summary>
    public bool PttSwallow
    {
        get => S.PttSwallow;
        set
        {
            S.PttSwallow = value;
            Save();
            _main.ReconfigureHotkeys();
        }
    }

    #endregion

    #region Public Methods

    /// <summary>Re-describes the speech recognition route.</summary>
    public void RefreshStt()
    {
        if (!_main.SttLocal)
        {
            SttStatus = S.Stt.Mode == "local" ? "SERVER (voice sidecar not up yet)" : "SERVER";
            return;
        }
        var model = _main.Game.Active ? S.Stt.GameModel + " · cpu (game mode)" : S.Stt.Model + " · cuda";
        var gating = _main.ServerUnderstandsUtterances ? "server gating" : "client gating (interim)";
        SttStatus = $"LOCAL · {model} · {gating}";
    }

    #endregion

    #region Commands

    /// <summary>Binds the next key pressed as push-to-talk (a combo makes no sense for hold-to-talk, so we keep the key).</summary>
    [RelayCommand]
    private void CapturePtt()
    {
        PttKey = "press a key…";
        Capturing = true;
        _main.Hotkeys.CaptureNext(name =>
        {
            S.PttKey = name.Split('+')[^1];
            _main.Store.Save();
            _main.ReconfigureHotkeys();
            PttKey = S.PttKey;
            Capturing = false;
        });
    }

    /// <summary>Binds the next key combination as the stop hotkey.</summary>
    [RelayCommand]
    private void CaptureStop()
    {
        StopHotkey = "press a combination…";
        Capturing = true;
        _main.Hotkeys.CaptureNext(name =>
        {
            S.StopHotkey = name;
            _main.Store.Save();
            _main.ReconfigureHotkeys();
            StopHotkey = S.StopHotkey;
            Capturing = false;
        });
    }

    #endregion

    #region Private Methods

    private static int Wrap(int value, int count) => (value % count + count) % count;

    private void RefreshDevices()
    {
        Fill(Inputs, DataFlow.Capture);
        Fill(Outputs, DataFlow.Render);
        OnPropertyChanged(nameof(InputIndex));
        OnPropertyChanged(nameof(OutputIndex));
    }

    private static void Fill(ObservableCollection<AudioDevice> devices, DataFlow flow)
    {
        devices.Clear();
        foreach (var device in AudioDevices.List(flow)) devices.Add(device);
    }

    #endregion
}
