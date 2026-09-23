using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TarsClient.Models;
using TarsClient.Services;

namespace TarsClient.ViewModels;

public sealed partial class ConfigRow : ObservableObject
{
    public required ConfigField Field { get; init; }
    public string Key => Field.Key;
    public string Description => Field.Description;
    public bool Secret => Field.Secret;
    public bool IsBool => Field.Type == "bool";
    public bool IsChoice => Field.Type.StartsWith("choice:");
    public string[] Choices => IsChoice ? Field.Type["choice:".Length..].Split(',') : [];
    public string Original { get; set; } = "";

    [ObservableProperty] public partial string Value { get; set; } = "";
    [ObservableProperty] public partial bool IsEditing { get; set; }
    [ObservableProperty] public partial string EditText { get; set; } = "";
    [ObservableProperty] public partial bool ServerPending { get; set; }

    public bool Changed => Value != Original;
    public bool Pending => Changed || ServerPending;
    public string Display => Secret && !Changed ? Value : IsBool ? (Value is "1" or "true" or "True" ? "ON" : "OFF") : Secret ? "••••(new)" : Value;

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(Pending));
        OnPropertyChanged(nameof(Changed));
    }
}

public sealed partial class LabCell : ObservableObject
{
    public required string Engine { get; init; }
    public required int Line { get; init; }
    public float[]? Samples { get; set; }
    [ObservableProperty] public partial string Timing { get; set; } = "---";
    [ObservableProperty] public partial int Stars { get; set; }
    public string StarText => new string('★', Stars) + new string('☆', 5 - Stars);
    partial void OnStarsChanged(int value) => OnPropertyChanged(nameof(StarText));
}

public sealed class LabRow
{
    public required string Kind { get; init; }
    public required string Text { get; init; }
    public required List<LabCell> Cells { get; init; }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    public static readonly string[] TabNames = ["PERSONALITY", "VOICE", "AUDIO", "SYSTEM", "SERVER", "FILES", "STATUS"];
    public static readonly string[] Engines = ["turbo", "chatterbox", "kokoro"];

    static readonly (string kind, string text)[] LabLines =
    [
        ("FACT", "Tau Ceti is eleven point nine light years from Earth."),
        ("NUMBER", "That comes to one thousand two hundred and forty seven rand."),
        ("TIMER", "Rice timer set for twelve minutes."),
        ("JOKE", "My humor setting is at seventy five percent. That was the funny part."),
        ("CORRECTION", "Actually, it's Nairobi. Mombasa is the one with the beach."),
        ("WEATHER", "Tomorrow, fog in the morning. High of nineteen, low of fourteen."),
        ("ALARM", "Pasta timer done. It is now, officially, al dente."),
        ("FILLER", "Checking."),
    ];

    readonly MainViewModel _main;
    readonly DispatcherTimer _statusPoll;
    bool _fromServer;
    TaskCompletionSource<bool>? _confirm;
    CancellationTokenSource? _labCts;

    ClientSettings S => _main.S;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        _statusPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusPoll.Tick += async (_, _) => await RefreshStatus();
        LabRows = LabLines.Select((l, i) => new LabRow
        {
            Kind = l.kind,
            Text = l.text,
            Cells = Engines.Select(e => new LabCell { Engine = e, Line = i, Stars = S.Tts.LabRatings.GetValueOrDefault($"{e}|{i}") }).ToList(),
        }).ToList();
    }

    [ObservableProperty] public partial int TabIndex { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial bool ConfirmVisible { get; set; }
    [ObservableProperty] public partial string ConfirmText { get; set; } = "";

    public string TabName => TabNames[TabIndex];

    partial void OnTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(TabName));
        Message = "";
        _ = OnTabShown();
    }

    public void NextTab(int dir) => TabIndex = (TabIndex + dir + TabNames.Length) % TabNames.Length;

    public void OnOpened()
    {
        PttKey = S.PttKey;
        StopHotkey = S.StopHotkey;
        RefreshDevices();
        RefreshVoice();
        _ = OnTabShown();
    }

    public void OnClosed() => _statusPoll.Stop();

    async Task OnTabShown()
    {
        _statusPoll.Stop();
        if (!_main.SettingsOpen) return;
        switch (TabName)
        {
            case "SERVER": if (ConfigRows.Count == 0) await LoadConfig(); break;
            case "FILES": if (Files.Count == 0) await LoadFiles(); break;
            case "STATUS": await RefreshStatus(); _statusPoll.Start(); break;
        }
    }

    void Say(string text) => Message = text;

    public Task<bool> ConfirmAsync(string text)
    {
        _confirm?.TrySetResult(false);
        _confirm = new TaskCompletionSource<bool>();
        ConfirmText = text;
        ConfirmVisible = true;
        return _confirm.Task;
    }

    [RelayCommand] void ConfirmYes() { ConfirmVisible = false; _confirm?.TrySetResult(true); }
    [RelayCommand] void ConfirmNo() { ConfirmVisible = false; _confirm?.TrySetResult(false); }

    // ================================================================== PERSONALITY

    [ObservableProperty] public partial double Humor { get; set; } = 75;
    [ObservableProperty] public partial double Honesty { get; set; } = 95;
    [ObservableProperty] public partial double Brevity { get; set; } = 85;

    partial void OnHumorChanged(double value) { if (!_fromServer) _main.PersonalityChanged(); }
    partial void OnHonestyChanged(double value) { if (!_fromServer) _main.PersonalityChanged(); }
    partial void OnBrevityChanged(double value) { if (!_fromServer) _main.PersonalityChanged(); }

    public void FromServer(JsonElement s)
    {
        _fromServer = true;
        try
        {
            if (s.TryGetProperty("humor", out var h) && h.TryGetDouble(out var hv)) Humor = hv;
            if (s.TryGetProperty("honesty", out var o) && o.TryGetDouble(out var ov)) Honesty = ov;
            if (s.TryGetProperty("brevity", out var b) && b.TryGetDouble(out var bv)) Brevity = bv;
        }
        finally { _fromServer = false; }
    }

    // ================================================================== VOICE

    [ObservableProperty] public partial string VoiceStatus { get; set; } = "";
    [ObservableProperty] public partial bool Installing { get; set; }
    public bool VoiceInstalled => LocalVoice.IsInstalled;
    public List<LabRow> LabRows { get; }

    public void RefreshVoice()
    {
        var v = _main.Voice;
        VoiceStatus = $"{v.State.ToString().ToUpperInvariant()}{(v.Detail != "" ? " · " + v.Detail : "")}";
        OnPropertyChanged(nameof(VoiceInstalled));
    }

    public int EngineIndex
    {
        get => Math.Max(0, Array.IndexOf(Engines, S.Tts.Engine));
        set
        {
            var e = Engines[(value + Engines.Length) % Engines.Length];
            if (e == S.Tts.Engine) return;
            S.Tts.Engine = e;
            _main.Store.Save();
            OnPropertyChanged();
            Say($"engine → {e}; restarting the voice sidecar");
            _main.Voice.RestartSidecar();
        }
    }

    public string ReferenceClip
    {
        get => S.Tts.ReferenceClip;
        set { S.Tts.ReferenceClip = value.Trim(); _main.Store.Save(); OnPropertyChanged(); }
    }

    public double Exaggeration { get => S.Tts.Exaggeration * 100; set { S.Tts.Exaggeration = value / 100; _main.Store.Save(); OnPropertyChanged(); } }
    public double Pace { get => S.Tts.Pace * 100; set { S.Tts.Pace = value / 100; _main.Store.Save(); OnPropertyChanged(); } }
    public bool Fx { get => S.Tts.Fx; set { S.Tts.Fx = value; _main.Store.Save(); OnPropertyChanged(); } }
    public double VoiceSpeed { get => Math.Round(S.Tts.Speed * 100); set { S.Tts.Speed = value / 100; _main.Store.Save(); OnPropertyChanged(); } }
    public double Temperature { get => Math.Round(S.Tts.Temperature * 100); set { S.Tts.Temperature = value / 100; _main.Store.Save(); OnPropertyChanged(); } }
    public double FxPitch { get => Math.Round(S.Tts.FxPitch * 100); set { S.Tts.FxPitch = value / 100; _main.Store.Save(); OnPropertyChanged(); } }
    public double FxRing { get => Math.Round(S.Tts.FxRing * 100); set { S.Tts.FxRing = value / 100; _main.Store.Save(); OnPropertyChanged(); } }

    [ObservableProperty] public partial string PreviewText { get; set; } = "Humor setting at seventy five percent. Honesty, ninety. That was not a joke.";

    /// <summary>Voice any line with the current settings, locally, without asking the server.</summary>
    [RelayCommand]
    async Task SpeakPreview()
    {
        if (string.IsNullOrWhiteSpace(PreviewText)) return;
        if (!_main.Voice.Healthy) { Say("voice sidecar offline"); return; }
        _main.Playback.Stop();
        Say($"speaking with {S.Tts.Engine}…");
        var r = await _main.Voice.StreamAsync(S.Tts.Engine, PreviewText.Trim(), 1.0, CancellationToken.None, TimeSpan.FromSeconds(60), collect: false);
        Say(r.Ok ? $"{S.Tts.Engine}: first audio {r.FirstAudioMs} ms" : "synthesis failed; see tts.log");
    }

    [RelayCommand]
    void ResetVoiceTuning()
    {
        var d = new TtsSettings();
        (S.Tts.Exaggeration, S.Tts.Pace, S.Tts.Speed, S.Tts.Temperature, S.Tts.FxPitch, S.Tts.FxRing, S.Tts.Fx) =
            (d.Exaggeration, d.Pace, d.Speed, d.Temperature, d.FxPitch, d.FxRing, d.Fx);
        _main.Store.Save();
        foreach (var n in new[] { nameof(Exaggeration), nameof(Pace), nameof(VoiceSpeed), nameof(Temperature), nameof(FxPitch), nameof(FxRing), nameof(Fx) })
            OnPropertyChanged(n);
        Say("voice tuning reset to defaults");
    }
    public bool GameModeEnabled { get => S.Tts.GameMode; set { S.Tts.GameMode = value; _main.Store.Save(); OnPropertyChanged(); } }
    public double IdleUnload { get => S.Tts.IdleUnloadMinutes; set { S.Tts.IdleUnloadMinutes = (int)value; _main.Store.Save(); OnPropertyChanged(); } }

    // ---- record your own reference clip

    public const string ReferenceScript =
        "Good evening. All systems are running within normal parameters. " +
        "The weather tomorrow is fog, with a high of nineteen degrees and a low of fourteen. " +
        "Your rice timer is set for twelve minutes. I checked the numbers twice. They are still the numbers. " +
        "Honesty setting: ninety percent. Humor: seventy-five. That was a joke. You'll know the next one by the pause.";

    [ObservableProperty] public partial bool Recording { get; set; }
    [ObservableProperty] public partial string RecordStatus { get; set; } = "";
    string? _lastRecording;
    CancellationTokenSource? _recCts;

    [RelayCommand]
    async Task RecordReference()
    {
        if (Recording) { _recCts?.Cancel(); return; }
        if (_main.Capture.IsEndpointMuted) { Say("the mic is muted in Windows: click MIC in the footer first"); return; }
        Recording = true;
        _recCts = new CancellationTokenSource();
        var rec = new ReferenceRecorder(_main.Capture);
        bool wasMuted = _main.MicMuted;
        _main.MicMuted = true;                         // don't send this to TARS
        _main.Playback.Stop();
        try
        {
            for (int i = 3; i > 0; i--) { RecordStatus = $"GET READY… {i}"; await Task.Delay(1000, _recCts.Token); }
            var started = DateTime.UtcNow;
            var ticker = Task.Run(async () =>
            {
                while (!_recCts.IsCancellationRequested)
                {
                    int left = 20 - (int)(DateTime.UtcNow - started).TotalSeconds;
                    int cells = (int)Math.Round(Math.Min(1, rec.Level * 1.4) * 10);
                    _main.Dispatch(() => RecordStatus = $"● REC {Math.Max(0, left):00}s  [{new string('█', cells)}{new string('░', 10 - cells)}]  read the script above");
                    await Task.Delay(150);
                }
            });
            var path = await rec.RecordAsync(TimeSpan.FromSeconds(20), Path.Combine(SettingsStore.Folder, "voices"), _recCts.Token);
            _recCts.Cancel();
            _lastRecording = path;
            ReferenceClip = path;
            RecordStatus = $"SAVED {Path.GetFileName(path)} · now TARS's reference voice · [PLAY IT] to check";
            _main.Voice.RestartSidecar();
        }
        catch (OperationCanceledException) { RecordStatus = "recording cancelled"; }
        catch (Exception ex) { RecordStatus = "FAILED: " + ex.Message; }
        finally
        {
            _recCts?.Cancel();
            _main.MicMuted = wasMuted;
            Recording = false;
        }
    }

    [RelayCommand]
    void PlayRecording()
    {
        var path = _lastRecording ?? (File.Exists(S.Tts.ReferenceClip) ? S.Tts.ReferenceClip : null);
        if (path == null) { RecordStatus = "nothing recorded yet"; return; }
        _main.Playback.Stop();
        _main.Playback.EnqueueWav(File.ReadAllBytes(path));
    }

    [RelayCommand]
    void BrowseReference()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "WAV audio|*.wav", Title = "Reference clip: 15–20 s of calm, dry reading" };
        if (dlg.ShowDialog() == true)
        {
            ReferenceClip = dlg.FileName;
            _main.Voice.RestartSidecar();
        }
    }

    [RelayCommand]
    void ResetReference()
    {
        ReferenceClip = "";
        try { File.Delete(LocalVoice.DefaultReference); } catch { }
        Say("default reference will be re-rendered locally");
        _main.Voice.RestartSidecar();
    }

    [RelayCommand]
    void TestVoice()
    {
        if (!_main.IsConnected) { Say("NO CARRIER"); return; }
        _main.Server.SendJson(new { type = "test_voice" });
        Say("test line requested");
    }

    [RelayCommand]
    async Task InstallVoice()
    {
        if (Installing) return;
        if (!await ConfirmAsync("Install the local voice engine? ~4.5 GB download (PyTorch CUDA + Chatterbox + Kokoro)")) return;
        Installing = true;
        Say("installing… see the scrollback");
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(LocalVoice.SidecarDir, "install.ps1") },
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _main.Dispatch(() => _main.Add(LineKind.Meta, "  " + e.Data)); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Write("install: " + e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            Say(p.ExitCode == 0 ? "voice engine installed" : $"install failed ({p.ExitCode}); see client.log");
            _main.Voice.RestartSidecar();
        }
        catch (Exception ex) { Say("install failed: " + ex.Message); }
        finally { Installing = false; RefreshVoice(); }
    }

    // ---- voice lab: the same 8 lines through every engine, for A/B by ear

    [RelayCommand]
    async Task LabRender()
    {
        _labCts?.Cancel();
        _labCts = new CancellationTokenSource();
        var ct = _labCts.Token;
        if (!_main.Voice.Healthy) { Say("voice sidecar offline"); return; }
        foreach (var engine in Engines)
            foreach (var row in LabRows)
            {
                if (ct.IsCancellationRequested) return;
                var cell = row.Cells.First(c => c.Engine == engine);
                cell.Timing = "…";
                Say($"rendering {engine}: {row.Kind}");
                var r = await _main.Voice.StreamAsync(engine, row.Text, 1.0, ct, TimeSpan.FromSeconds(90), collect: true, play: false);
                cell.Samples = r.Samples;
                cell.Timing = r.Ok ? $"{r.FirstAudioMs} ms" : "FAIL";
            }
        Say("lab rendered: play and rate");
    }

    [RelayCommand]
    async Task LabPlay(LabCell cell)
    {
        _main.Playback.Stop();
        if (cell.Samples == null)
        {
            var row = LabRows[cell.Line];
            var r = await _main.Voice.StreamAsync(cell.Engine, row.Text, 1.0, CancellationToken.None, TimeSpan.FromSeconds(90), collect: true, play: false);
            cell.Samples = r.Samples;
            cell.Timing = r.Ok ? $"{r.FirstAudioMs} ms" : "FAIL";
        }
        if (cell.Samples != null) _main.Playback.Enqueue(cell.Samples);
    }

    [RelayCommand]
    void LabRate(LabCell cell)
    {
        cell.Stars = cell.Stars >= 5 ? 0 : cell.Stars + 1;
        S.Tts.LabRatings[$"{cell.Engine}|{cell.Line}"] = cell.Stars;
        _main.Store.Save();
        var totals = Engines.Select(e => (e, LabRows.Sum(r => r.Cells.First(c => c.Engine == e).Stars))).OrderByDescending(t => t.Item2).ToList();
        Say("totals: " + string.Join(" · ", totals.Select(t => $"{t.e} {t.Item2}")));
    }

    // ================================================================== AUDIO

    public ObservableCollection<AudioDevice> Inputs { get; } = [];
    public ObservableCollection<AudioDevice> Outputs { get; } = [];

    void RefreshDevices()
    {
        void Fill(ObservableCollection<AudioDevice> col, NAudio.CoreAudioApi.DataFlow flow)
        {
            col.Clear();
            foreach (var d in AudioDevices.List(flow)) col.Add(d);
        }
        Fill(Inputs, NAudio.CoreAudioApi.DataFlow.Capture);
        Fill(Outputs, NAudio.CoreAudioApi.DataFlow.Render);
        OnPropertyChanged(nameof(InputIndex));
        OnPropertyChanged(nameof(OutputIndex));
    }

    public int InputIndex
    {
        get => Math.Max(0, Inputs.ToList().FindIndex(d => d.Id == S.InputDevice));
        set
        {
            if (Inputs.Count == 0) return;
            var d = Inputs[(value + Inputs.Count) % Inputs.Count];
            S.InputDevice = d.Id;
            _main.Store.Save();
            _main.Capture.Start(d.Id);
            OnPropertyChanged();
        }
    }

    public int OutputIndex
    {
        get => Math.Max(0, Outputs.ToList().FindIndex(d => d.Id == S.OutputDevice));
        set
        {
            if (Outputs.Count == 0) return;
            var d = Outputs[(value + Outputs.Count) % Outputs.Count];
            S.OutputDevice = d.Id;
            _main.Store.Save();
            _main.Playback.Open(d.Id);
            OnPropertyChanged();
        }
    }

    // ---- speech recognition
    public static readonly string[] SttModes = ["LOCAL (this PC)", "SERVER"];
    public static readonly string[] SttModels = ["large-v3-turbo", "small.en"];

    public int SttModeIndex
    {
        get => S.Stt.Mode == "server" ? 1 : 0;
        set
        {
            S.Stt.Mode = (value % 2 + 2) % 2 == 1 ? "server" : "local";
            _main.Store.Save();
            _main.UpdateSttRoute();
            OnPropertyChanged();
        }
    }

    public int SttModelIndex
    {
        get => Math.Max(0, Array.IndexOf(SttModels, S.Stt.Model));
        set
        {
            S.Stt.Model = SttModels[(value % SttModels.Length + SttModels.Length) % SttModels.Length];
            _main.Store.Save();
            _main.Stt.Reconfigure();
            OnPropertyChanged();
        }
    }

    [ObservableProperty] public partial string SttStatus { get; set; } = "";

    public void RefreshStt()
    {
        SttStatus = !_main.SttLocal
            ? (S.Stt.Mode == "local" ? "SERVER (voice sidecar not up yet)" : "SERVER")
            : $"LOCAL · {(_main.Game.Active ? S.Stt.GameModel + " · cpu (game mode)" : S.Stt.Model + " · cuda")} · " +
              (_main.ServerUnderstandsUtterances ? "server gating" : "client gating (interim)");
    }

    public double EchoTail { get => S.EchoTailMs; set { S.EchoTailMs = (int)value; _main.Store.Save(); OnPropertyChanged(); } }

    [ObservableProperty] public partial string PttKey { get; set; } = "";
    [ObservableProperty] public partial string StopHotkey { get; set; } = "";
    [ObservableProperty] public partial bool Capturing { get; set; }

    public bool PttSwallow { get => S.PttSwallow; set { S.PttSwallow = value; _main.Store.Save(); _main.ReconfigureHotkeys(); OnPropertyChanged(); } }

    [RelayCommand]
    void CapturePtt()
    {
        PttKey = "press a key or mouse button…";
        Capturing = true;
        _main.Hotkeys.CaptureNext(name =>
        {
            // A combo makes no sense for hold-to-talk: keep the key itself.
            S.PttKey = name.Split('+')[^1];
            _main.Store.Save();
            _main.ReconfigureHotkeys();
            PttKey = S.PttKey;
            Capturing = false;
        });
    }

    [RelayCommand]
    void CaptureStop()
    {
        StopHotkey = "press a combination…";
        Capturing = true;
        _main.Hotkeys.CaptureNext(name =>
        {
            if (!name.StartsWith("Mouse")) { S.StopHotkey = name; _main.Store.Save(); _main.ReconfigureHotkeys(); }
            StopHotkey = S.StopHotkey;
            Capturing = false;
        });
    }

    // ================================================================== SYSTEM

    public string ServerUrl { get => S.ServerUrl; set { S.ServerUrl = value.Trim(); _main.Store.Save(); OnPropertyChanged(); } }
    public string AccessKey
    {
        get => S.AccessKey;
        set
        {
            if (S.AccessKey == value.Trim()) return;
            S.AccessKey = value.Trim();
            _main.Store.Save();
            OnPropertyChanged();
            if (_main.Server.IsDenied || !_main.IsConnected) _main.Server.Restart();   // a new key: try it now
        }
    }

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set { S.StartWithWindows = value; _main.Store.Save(); SettingsStore.ApplyStartWithWindows(value, S.StartMinimized); OnPropertyChanged(); }
    }

    public bool StartMinimized
    {
        get => S.StartMinimized;
        set { S.StartMinimized = value; _main.Store.Save(); SettingsStore.ApplyStartWithWindows(S.StartWithWindows, value); OnPropertyChanged(); }
    }

    public bool Topmost { get => _main.Topmost; set { if (value != _main.Topmost) _main.TogglePinCommand.Execute(null); OnPropertyChanged(); } }
    public bool Scanlines { get => S.Scanlines; set { S.Scanlines = value; Apply(); } }
    public bool Glow { get => S.Glow; set { S.Glow = value; Apply(); } }
    public double FontSize { get => S.FontSize; set { S.FontSize = value; Apply(); } }
    public double TypewriterCps { get => S.TypewriterCps; set { S.TypewriterCps = (int)value; Apply(); } }
    public bool ShowDetails { get => S.ShowDetails; set { S.ShowDetails = value; Apply(); } }
    public bool ShowInTaskbar { get => S.ShowInTaskbar; set { S.ShowInTaskbar = value; Apply(); } }
    public bool PopOnReply { get => S.PopOnReply; set { S.PopOnReply = value; Apply(); } }
    public bool NoActivate { get => S.NoActivate; set { S.NoActivate = value; Apply(); } }

    public double BackgroundOpacity
    {
        get => Math.Round(S.BackgroundOpacity * 100);
        set
        {
            bool wasOpaque = S.BackgroundOpacity >= 0.999;
            S.BackgroundOpacity = Math.Clamp(value, 20, 100) / 100;
            Apply();
            if (wasOpaque && S.BackgroundOpacity < 0.999) Say("transparency needs a restart: [RESTART TARS]");
        }
    }

    [RelayCommand]
    async Task Uninstall()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TARS");
        var exe = new[] { Path.Combine(dir, "TARS-Uninstall.exe"), Path.Combine(dir, "TARS-Setup.exe") }.FirstOrDefault(File.Exists);
        if (exe == null) { Say("no installed copy found (this looks like a dev build)"); return; }
        if (!await ConfirmAsync("Uninstall TARS Desktop? TARS will close and the uninstaller will open.")) return;
        _main.Store.SaveNow();
        Process.Start(new ProcessStartInfo(exe, "--uninstall") { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        System.Windows.Application.Current.Shutdown();
    }

    [RelayCommand]
    void RestartApp()
    {
        _main.Store.SaveNow();
        var exe = Environment.ProcessPath!;
        // Wait for this instance (and its single-instance mutex) to go before starting again.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        System.Windows.Application.Current.Shutdown();
    }
    public bool SpeakTyped { get => S.SpeakTyped; set { S.SpeakTyped = value; Apply(); } }

    void Apply([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        _main.Store.Save();
        _main.ApplyAppearance();
        OnPropertyChanged(name);
    }

    [RelayCommand]
    async Task TestConnection()
    {
        Say("dialing…");
        try
        {
            var h = await _main.Admin.HealthAsync();
            Say($"OK · {h.GetProperty("name")} · llm {(h.TryGetProperty("llm_ready", out var l) && l.GetBoolean() ? "ready" : "down")} · cloud {h.GetProperty("cloud")}");
        }
        catch (Exception ex) { Say(ex.Message); }
    }

    [RelayCommand]
    async Task FetchKey()
    {
        var key = await _main.Admin.FetchKeyFromSpecAsync();
        if (key == null) { Say("could not fetch the key (is this PC allowlisted?)"); return; }
        AccessKey = key;
        Say("access key fetched and sealed with DPAPI");
    }

    [RelayCommand]
    void Reconnect()
    {
        _main.Server.Restart();
        Say("reconnecting…");
    }

    // ================================================================== SERVER (admin config)

    public ObservableCollection<ConfigRow> ConfigRows { get; } = [];
    [ObservableProperty] public partial bool RestartRequired { get; set; }

    [RelayCommand]
    async Task LoadConfig()
    {
        Say("loading config…");
        try
        {
            var cfg = await _main.Admin.GetConfigAsync();
            ConfigRows.Clear();
            foreach (var f in cfg.Fields)
                ConfigRows.Add(new ConfigRow { Field = f, Original = f.Value, Value = f.Value, ServerPending = f.PendingRestart });
            RestartRequired = cfg.RestartRequired;
            Say($"{cfg.Fields.Count} fields{(cfg.RestartRequired ? " · RESTART REQUIRED" : "")}");
        }
        catch (Exception ex) { Say(ex.Message); }
    }

    /// <summary>Enter on a row: bools toggle, choices cycle, strings/secrets open an inline editor.</summary>
    [RelayCommand]
    void EditRow(ConfigRow row)
    {
        if (row.IsBool) { row.Value = row.Value is "1" or "true" or "True" ? "0" : "1"; return; }
        if (row.IsChoice)
        {
            var i = Array.IndexOf(row.Choices, row.Value);
            row.Value = row.Choices[(i + 1) % row.Choices.Length];
            return;
        }
        row.EditText = row.Secret ? "" : row.Value;
        row.IsEditing = true;
    }

    [RelayCommand]
    void CommitRow(ConfigRow row)
    {
        row.IsEditing = false;
        if (row.Secret && row.EditText.Length == 0) return;     // empty = keep the current secret
        row.Value = row.EditText.Trim();
    }

    [RelayCommand]
    void CancelRow(ConfigRow row) => row.IsEditing = false;

    [RelayCommand]
    Task ApplyConfig() => SendConfig(restart: false);

    [RelayCommand]
    Task ApplyRestart() => SendConfig(restart: true);

    async Task SendConfig(bool restart)
    {
        var changes = ConfigRows.Where(r => r.Changed).ToDictionary(r => r.Key, r => r.Value);
        if (changes.Count == 0 && !restart) { Say("nothing changed"); return; }

        if (changes.TryGetValue("TARS_KEY", out var newKey))
        {
            if (!await ConfirmAsync("Change TARS_KEY? The client stores the new key first, then updates the server.")) return;
            AccessKey = newKey;
        }
        if (changes.TryGetValue("ALLOWED_CLIENTS", out var allowed) && !IncludesThisPc(allowed))
        {
            if (!await ConfirmAsync("ALLOWED_CLIENTS no longer lists this PC. It will need the access key to connect. Continue?")) return;
        }

        Say(restart ? "applying + restarting…" : "applying…");
        try
        {
            if (changes.Count > 0) await _main.Admin.PatchConfigAsync(changes, restart);
            else await _main.Admin.RestartAsync();
            Say(restart ? "server restarting: NO CARRIER expected, back in ~10 s" : "saved: fields marked PENDING RESTART");
            if (restart)
            {
                ConfigRows.Clear();
                await Task.Delay(12000);
                if (TabName == "SERVER" && _main.SettingsOpen) await LoadConfig();
            }
            else await LoadConfig();
        }
        catch (Exception ex) { Say(ex.Message); }
    }

    static bool IncludesThisPc(string list)
    {
        var entries = list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                          .Select(e => e.ToLowerInvariant()).ToHashSet();
        var host = Dns.GetHostName().ToLowerInvariant();
        if (entries.Contains(host) || entries.Contains(host + ".lan")) return true;
        try
        {
            return Dns.GetHostAddresses(host).Any(a => entries.Contains(a.ToString()));
        }
        catch { return false; }
    }

    // ================================================================== FILES

    public ObservableCollection<ServerFile> Files { get; } = [];
    [ObservableProperty] public partial ServerFile? SelectedFile { get; set; }
    [ObservableProperty] public partial string EditorText { get; set; } = "";
    [ObservableProperty] public partial bool Dirty { get; set; }
    [ObservableProperty] public partial string NewFileName { get; set; } = "";
    bool _loadingFile;

    partial void OnSelectedFileChanged(ServerFile? value) => _ = OpenFile(value);
    partial void OnEditorTextChanged(string value) { if (!_loadingFile) Dirty = true; }

    [RelayCommand]
    async Task LoadFiles()
    {
        try
        {
            var files = await _main.Admin.GetFilesAsync();
            var keep = SelectedFile;
            Files.Clear();
            foreach (var f in files.OrderBy(f => f.Area == "persona" ? 0 : 1).ThenBy(f => f.Name)) Files.Add(f);
            Say($"{files.Count} files");
            if (keep != null) SelectedFile = Files.FirstOrDefault(f => f.Area == keep.Area && f.Name == keep.Name);
        }
        catch (Exception ex) { Say(ex.Message); }
    }

    async Task OpenFile(ServerFile? f)
    {
        if (f == null) return;
        try
        {
            var text = await _main.Admin.GetFileAsync(f.Area, f.Name);
            _loadingFile = true;
            EditorText = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            _loadingFile = false;
            Dirty = false;
            Say($"{f.Area}/{f.Name} · {f.Bytes} bytes");
        }
        catch (Exception ex) { Say(ex.Message); }
    }

    [RelayCommand]
    async Task SaveFile()
    {
        if (SelectedFile is not { } f) return;
        try
        {
            await _main.Admin.PutFileAsync(f.Area, f.Name, EditorText.Replace("\r\n", "\n"));
            Dirty = false;
            Say(f.Area == "knowledge" ? $"saved {f.Name} · re-indexed" : $"saved {f.Name} · applies on the next answer");
        }
        catch (Exception ex) { Say(ex.Message); }
    }

    [RelayCommand]
    async Task NewFile()
    {
        var name = NewFileName.Trim();
        if (name.Length == 0) { Say("type a name first, e.g. cars.md"); return; }
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) name += ".md";
        try
        {
            await _main.Admin.PutFileAsync("knowledge", name, $"# {Path.GetFileNameWithoutExtension(name)}\n");
            NewFileName = "";
            await LoadFiles();
            SelectedFile = Files.FirstOrDefault(x => x.Area == "knowledge" && x.Name == name);
        }
        catch (Exception ex) { Say(ex.Message); }
    }

    [RelayCommand]
    async Task DeleteFile()
    {
        if (SelectedFile is not { } f) return;
        if (!f.Deletable) { Say($"{f.Name} is protected"); return; }
        if (!await ConfirmAsync($"Delete knowledge/{f.Name}? This cannot be undone.")) return;
        try
        {
            await _main.Admin.DeleteFileAsync(f.Name);
            SelectedFile = null;
            _loadingFile = true;
            EditorText = "";
            _loadingFile = false;
            Dirty = false;
            await LoadFiles();
            Say($"deleted {f.Name}");
        }
        catch (Exception ex) { Say(ex.Message); }
    }

    // ================================================================== STATUS

    [ObservableProperty] public partial string StatusSummary { get; set; } = "";
    public ObservableCollection<string> LogLines { get; } = [];
    bool _polling;

    async Task RefreshStatus()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var st = await _main.Admin.GetStatusAsync(200);
            var up = TimeSpan.FromSeconds(st.TryGetProperty("uptime_s", out var u) ? u.GetDouble() : 0);
            var h = st.GetProperty("health");
            string J(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ToString() : "-";
            var ollama = st.TryGetProperty("ollama_loaded", out var ol) ? string.Join(", ", ol.EnumerateArray().Select(x => x.ToString())) : "-";
            var clients = st.TryGetProperty("clients", out var cl) && cl.ValueKind == JsonValueKind.Array
                ? string.Join(", ", cl.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Object ? J(c, "name") + "@" + J(c, "ip") : c.ToString()))
                : "-";
            var timers = st.TryGetProperty("timers", out var tm) && tm.ValueKind == JsonValueKind.Array ? tm.GetArrayLength() : 0;
            StatusSummary =
                $"UPTIME ......... {(int)up.TotalHours}h {up:mm}m {up:ss}s\n" +
                $"LOCAL MODEL .... {J(h, "model")} (loaded: {ollama})\n" +
                $"CLOUD .......... {J(h, "cloud")} · stt {J(h, "cloud_stt")} · cooldown {J(st, "cloud_cooldown_s")} s\n" +
                $"HOME ASSISTANT . {J(h, "ha")}\n" +
                $"CLIENTS ........ {(clients == "" ? "none" : clients)}\n" +
                $"TIMERS ......... {timers}\n" +
                $"LOCAL VOICE .... {VoiceStatus}";
            if (st.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.Array)
            {
                var lines = log.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                if (LogLines.Count == 0 || LogLines[^1] != lines.LastOrDefault())
                {
                    LogLines.Clear();
                    foreach (var l in lines) LogLines.Add(l);
                }
            }
        }
        catch (Exception ex) { Say(ex.Message); }
        finally { _polling = false; }
    }

    [RelayCommand]
    async Task RestartServer()
    {
        if (!await ConfirmAsync("Restart the TARS server? Every client drops for ~10 s.")) return;
        try { await _main.Admin.RestartAsync(); Say("restarting…"); }
        catch (Exception ex) { Say(ex.Message); }
    }
}
