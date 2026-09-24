using System.Diagnostics;

namespace TarsClient.ViewModels;

/// <summary>VOICE tab: engine, tuning, reference clip, preview and the voice engine install.</summary>
public sealed partial class SettingsViewModel
{
    #region Fields

    /// <summary>The local TTS engines, in the order the engine row cycles through them.</summary>
    public static readonly string[] Engines = ["turbo", "chatterbox", "kokoro"];

    #endregion

    #region Properties

    /// <summary>The sidecar's state, as one line.</summary>
    [ObservableProperty]
    public partial string VoiceStatus { get; set; } = "";

    /// <summary>The install script is running.</summary>
    [ObservableProperty]
    public partial bool Installing { get; set; }

    /// <summary>A line to try the current settings on.</summary>
    [ObservableProperty]
    public partial string PreviewText { get; set; } = "Humor setting at seventy five percent. Honesty, ninety. That was not a joke.";

    /// <summary>The Python venv and sidecar are present.</summary>
    public bool VoiceInstalled => LocalVoice.IsInstalled;

    /// <summary>The selected engine; a change loads it in the background.</summary>
    public int EngineIndex
    {
        get => Math.Max(0, Array.IndexOf(Engines, S.Tts.Engine));
        set
        {
            var engine = Engines[(value + Engines.Length) % Engines.Length];
            if (engine == S.Tts.Engine) return;
            S.Tts.Engine = engine;
            Save();
            Say($"engine → {engine}: loading in the background (kokoro covers replies meanwhile)");
            _main.Voice.SwitchEngine(engine);
        }
    }

    /// <summary>The reference clip; every request names it, so no restart is needed.</summary>
    public string ReferenceClip
    {
        get => S.Tts.ReferenceClip;
        set
        {
            S.Tts.ReferenceClip = value.Trim();
            Save();
        }
    }

    /// <summary>Chatterbox exaggeration, percent.</summary>
    public double Exaggeration { get => S.Tts.Exaggeration * 100; set { S.Tts.Exaggeration = value / 100; Save(); } }

    /// <summary>Chatterbox pace (cfg weight), percent.</summary>
    public double Pace { get => S.Tts.Pace * 100; set { S.Tts.Pace = value / 100; Save(); } }

    /// <summary>The TARS radio/robot effect.</summary>
    public bool Fx { get => S.Tts.Fx; set { S.Tts.Fx = value; Save(); } }

    /// <summary>Speaking speed, percent.</summary>
    public double VoiceSpeed { get => Math.Round(S.Tts.Speed * 100); set { S.Tts.Speed = value / 100; Save(); } }

    /// <summary>Sampling temperature, percent.</summary>
    public double Temperature { get => Math.Round(S.Tts.Temperature * 100); set { S.Tts.Temperature = value / 100; Save(); } }

    /// <summary>FX pitch, percent.</summary>
    public double FxPitch { get => Math.Round(S.Tts.FxPitch * 100); set { S.Tts.FxPitch = value / 100; Save(); } }

    /// <summary>FX ring, percent.</summary>
    public double FxRing { get => Math.Round(S.Tts.FxRing * 100); set { S.Tts.FxRing = value / 100; Save(); } }

    /// <summary>Hand the GPU back while a game runs.</summary>
    public bool GameModeEnabled { get => S.Tts.GameMode; set { S.Tts.GameMode = value; Save(); } }

    /// <summary>Minutes idle before the GPU voice unloads.</summary>
    public double IdleUnload { get => S.Tts.IdleUnloadMinutes; set { S.Tts.IdleUnloadMinutes = (int)value; Save(); } }

    #endregion

    #region Public Methods

    /// <summary>Re-reads the sidecar's state.</summary>
    public void RefreshVoice()
    {
        var voice = _main.Voice;
        VoiceStatus = voice.State.ToString().ToUpperInvariant() + (voice.Detail != "" ? " · " + voice.Detail : "");
        OnPropertyChanged(nameof(VoiceInstalled));
    }

    #endregion

    #region Commands

    /// <summary>Voices <see cref="PreviewText"/> with the current settings, locally, without asking the server.</summary>
    [RelayCommand]
    private async Task SpeakPreview()
    {
        if (string.IsNullOrWhiteSpace(PreviewText)) return;
        if (!_main.Voice.Healthy)
        {
            Say("voice sidecar offline");
            return;
        }
        _main.Playback.Stop();
        Say($"speaking with {S.Tts.Engine}…");
        var result = await _main.Voice.StreamAsync(S.Tts.Engine, PreviewText.Trim(), 1.0, CancellationToken.None, TimeSpan.FromSeconds(60), collect: false);
        Say(result.Ok ? $"{S.Tts.Engine}: first audio {result.FirstAudioMs} ms" : "synthesis failed; see tts.log");
    }

    [RelayCommand]
    private void ResetVoiceTuning()
    {
        var defaults = new TtsSettings();
        (S.Tts.Exaggeration, S.Tts.Pace, S.Tts.Speed, S.Tts.Temperature, S.Tts.FxPitch, S.Tts.FxRing, S.Tts.Fx) =
            (defaults.Exaggeration, defaults.Pace, defaults.Speed, defaults.Temperature, defaults.FxPitch, defaults.FxRing, defaults.Fx);
        _main.Store.Save();
        foreach (var name in (string[])[nameof(Exaggeration), nameof(Pace), nameof(VoiceSpeed), nameof(Temperature), nameof(FxPitch), nameof(FxRing), nameof(Fx)])
        {
            OnPropertyChanged(name);
        }
        Say("voice tuning reset to defaults");
    }

    [RelayCommand]
    private void BrowseReference()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "WAV audio|*.wav", Title = "Reference clip: 15–20 s of calm, dry reading" };
        if (dialog.ShowDialog() == true) ReferenceClip = dialog.FileName;
    }

    [RelayCommand]
    private void ResetReference()
    {
        ReferenceClip = "";
        TryDelete(LocalVoice.DefaultReference);
        Say("back to the default voice (re-rendered locally if missing)");
    }

    /// <summary>Asks the server for its test line (the server writes it; we voice it).</summary>
    [RelayCommand]
    private void TestVoice()
    {
        if (!_main.IsConnected)
        {
            Say("NO CARRIER");
            return;
        }
        _main.ExpectAudio();
        _main.Server.SendJson(ClientMessages.TestVoice());
        Say("test line requested");
    }

    [RelayCommand]
    private async Task InstallVoice()
    {
        if (Installing) return;
        if (!await ConfirmAsync("Install the local voice engine? ~4.5 GB download (PyTorch CUDA + Chatterbox + Kokoro)")) return;
        Installing = true;
        Say("installing… see the scrollback");
        try
        {
            int exitCode = await RunInstallScriptAsync();
            Say(exitCode == 0 ? "voice engine installed" : $"install failed ({exitCode}); see client.log");
            _main.Voice.RestartSidecar();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Say("install failed: " + ex.Message);
        }
        finally
        {
            Installing = false;
            RefreshVoice();
        }
    }

    #endregion

    #region Private Methods

    private async Task<int> RunInstallScriptAsync()
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(LocalVoice.SidecarDir, "install.ps1") },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) _main.Dispatch(() => _main.Add(LineKind.Meta, "  " + e.Data));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Log.Write("install: " + e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"delete {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    #endregion
}
