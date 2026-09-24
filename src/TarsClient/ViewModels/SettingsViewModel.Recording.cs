using System.Net.Http.Json;

namespace TarsClient.ViewModels;

/// <summary>VOICE tab, continued: recording a reference clip, and training the voice lock on your voice.</summary>
public sealed partial class SettingsViewModel
{
    #region Fields

    /// <summary>What to read for a reference clip or voice training: varied, calm, about 20 seconds.</summary>
    public const string ReferenceScript =
        "Good evening. All systems are running within normal parameters. " +
        "The weather tomorrow is fog, with a high of nineteen degrees and a low of fourteen. " +
        "Your rice timer is set for twelve minutes. I checked the numbers twice. They are still the numbers. " +
        "Honesty setting: ninety percent. Humor: seventy-five. That was a joke. You'll know the next one by the pause.";

    private static readonly TimeSpan RecordLength = TimeSpan.FromSeconds(20);
    private static readonly string VoicesFolder = Path.Combine(SettingsStore.Folder, "voices");

    private string? _lastRecording;
    private CancellationTokenSource? _recCts;

    #endregion

    #region Properties

    /// <summary>A recording is running (the button becomes cancel).</summary>
    [ObservableProperty]
    public partial bool Recording { get; set; }

    /// <summary>Reference recording progress.</summary>
    [ObservableProperty]
    public partial string RecordStatus { get; set; } = "";

    /// <summary>Voice training progress.</summary>
    [ObservableProperty]
    public partial string EnrollStatus { get; set; } = "";

    /// <summary>Hands-free turns need your voice. It can only be turned on once a voiceprint exists.</summary>
    public bool VoiceLock
    {
        get => S.Stt.VoiceLock;
        set
        {
            if (value && !File.Exists(S.Stt.Voiceprint))
            {
                Say("train it first: [TRAIN ON MY VOICE]");
                OnPropertyChanged();
                return;
            }
            S.Stt.VoiceLock = value;
            Save();
            _main.Stt.Reconfigure();
        }
    }

    /// <summary>How close to your voiceprint speech must be, percent.</summary>
    public double SpeakerThreshold
    {
        get => Math.Round(S.Stt.SpeakerThreshold * 100);
        set
        {
            S.Stt.SpeakerThreshold = value / 100;
            Save();
            _main.Stt.Reconfigure();
        }
    }

    #endregion

    #region Commands

    /// <summary>Records 20 s of you reading <see cref="ReferenceScript"/> and makes it TARS's reference voice.</summary>
    [RelayCommand]
    private async Task RecordReference()
    {
        if (Recording)
        {
            _recCts?.Cancel();
            return;
        }
        if (_main.Capture.IsEndpointMuted)
        {
            Say("the mic is muted in Windows: click MIC in the footer first");
            return;
        }
        try
        {
            var path = await RecordClipAsync(s => RecordStatus = s, "", "REC", "  read the script above");
            _lastRecording = path;
            ReferenceClip = path;
            RecordStatus = $"SAVED {Path.GetFileName(path)} · now TARS's reference voice · [PLAY IT] to check";
        }
        catch (OperationCanceledException)
        {
            RecordStatus = "recording cancelled";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            RecordStatus = "FAILED: " + ex.Message;
        }
    }

    /// <summary>Records you, has the sidecar build a voiceprint from it, and turns the voice lock on. Only the voiceprint is kept.</summary>
    [RelayCommand]
    private async Task TrainVoice()
    {
        if (Recording)
        {
            _recCts?.Cancel();
            return;
        }
        if (!_main.Voice.Healthy)
        {
            EnrollStatus = "the voice engine isn't running";
            return;
        }
        if (_main.Capture.IsEndpointMuted)
        {
            EnrollStatus = "the mic is muted in Windows: click MIC in the footer first";
            return;
        }
        try
        {
            var wav = await RecordClipAsync(s => EnrollStatus = s, "  (read the script above in your normal voice)", "LISTENING", "");
            EnrollStatus = "building voiceprint…";
            var voiceprint = Path.Combine(VoicesFolder, "voiceprint.npy");
            var error = await EnrollAsync(wav, voiceprint);
            TryDelete(wav);
            if (error != null)
            {
                EnrollStatus = "NOT SAVED: " + error + " · voice lock unchanged";
                return;
            }
            S.Stt.Voiceprint = voiceprint;
            S.Stt.VoiceLock = true;
            _main.Store.Save();
            _main.Stt.Reconfigure();
            OnPropertyChanged(nameof(VoiceLock));
            EnrollStatus = "VOICE LOCK ON: hands-free turns now need your voice (push-to-talk always works)";
        }
        catch (OperationCanceledException)
        {
            EnrollStatus = "cancelled";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException)
        {
            EnrollStatus = "FAILED: " + ex.Message;
        }
    }

    [RelayCommand]
    private void PlayRecording()
    {
        var path = _lastRecording ?? (File.Exists(S.Tts.ReferenceClip) ? S.Tts.ReferenceClip : null);
        if (path == null)
        {
            RecordStatus = "nothing recorded yet";
            return;
        }
        _main.Playback.Stop();
        _main.Playback.EnqueueWav(File.ReadAllBytes(path));
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// A 3-2-1 countdown, then 20 s from the mic to a WAV in the voices folder, with a live level bar. The mic is
    /// muted towards TARS meanwhile so the reading doesn't become a turn.
    /// </summary>
    private async Task<string> RecordClipAsync(Action<string> status, string readyHint, string label, string liveHint)
    {
        Recording = true;
        _recCts = new CancellationTokenSource();
        var ct = _recCts.Token;
        var recorder = new ReferenceRecorder(_main.Capture);
        bool wasMuted = _main.MicMuted;
        _main.MicMuted = true;
        _main.Playback.Stop();
        try
        {
            for (int i = 3; i > 0; i--)
            {
                status($"GET READY… {i}{readyHint}");
                await Task.Delay(1000, ct);
            }
            _ = ShowLevelAsync(recorder, status, label, liveHint, ct);
            return await recorder.RecordAsync(RecordLength, VoicesFolder, ct);
        }
        finally
        {
            _recCts.Cancel();
            _main.MicMuted = wasMuted;
            Recording = false;
        }
    }

    private static async Task ShowLevelAsync(ReferenceRecorder recorder, Action<string> status, string label, string liveHint, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            while (true)
            {
                int left = Math.Max(0, (int)(RecordLength - (DateTime.UtcNow - started)).TotalSeconds);
                int cells = (int)Math.Round(Math.Min(1, recorder.Level * 1.4) * 10);
                status($"● {label} {left:00}s  [{new string('█', cells)}{new string('░', 10 - cells)}]{liveHint}");
                await Task.Delay(150, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The recording ended.
        }
    }

    /// <summary>Asks the sidecar to build a voiceprint; returns its error, or null when it's saved.</summary>
    private async Task<string?> EnrollAsync(string wav, string voiceprint)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var response = await http.PostAsync(S.Tts.SidecarUrl.TrimEnd('/') + "/stt/enroll", JsonContent.Create(new { wav, @out = voiceprint }));
        if (response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            return JsonDocument.Parse(body).RootElement.GetProperty("error").GetString() ?? body;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return body;
        }
    }

    #endregion
}
