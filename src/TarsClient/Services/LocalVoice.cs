using System.Diagnostics;
using System.Net.Http.Json;

namespace TarsClient.Services;

/// <summary>
/// TARS's voice, generated entirely on this PC. This half runs the Python sidecar: we start it hidden in a
/// kill-on-close job, health-check it every 5 s, back off when it crash-loops, switch engines without restarting it,
/// unload the GPU model after idle minutes and follow game mode. The other half (<c>LocalVoice.Speaking.cs</c>)
/// voices <c>say</c> lines.
/// </summary>
public sealed partial class LocalVoice : IDisposable
{
    #region Fields

    private static readonly string TtsLog = Path.Combine(SettingsStore.Folder, "tts.log");

    private readonly Func<ClientSettings> _settings;
    private readonly AudioPlayback _playback;
    private readonly GameMode _game;
    private readonly Dispatcher _ui;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly IntPtr _job = Win32.CreateKillOnCloseJob();
    private readonly DispatcherTimer _healthTimer;
    private Process? _proc;
    private CancellationTokenSource? _switchCts;
    private DateTime _lastSay = DateTime.UtcNow;
    private DateTime _nextStartAttempt = DateTime.MinValue;
    private int _startFailures;
    private bool _makingReference;
    private string[] _loaded = [];
    private string[] _loading = [];
    private double _vramMb;

    #endregion

    #region Constructor

    /// <summary>Creates the voice; playback and events run on <paramref name="ui"/>.</summary>
    public LocalVoice(Dispatcher ui, Func<ClientSettings> settings, AudioPlayback playback, GameMode game)
    {
        _ui = ui;
        _settings = settings;
        _playback = playback;
        _game = game;
        _game.Changed += OnGameModeChanged;
        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _healthTimer.Tick += async (_, _) => await TickAsync();
    }

    #endregion

    #region Properties

    /// <summary>The venv interpreter the install script creates.</summary>
    public static string VenvPython { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TARS", "tts-venv", "Scripts", "python.exe");

    /// <summary>The sidecar scripts shipped next to the exe.</summary>
    public static string SidecarDir { get; } = Path.Combine(AppContext.BaseDirectory, "tts-sidecar");

    /// <summary>The default reference voice, rendered locally by Kokoro on first run.</summary>
    public static string DefaultReference { get; } = Path.Combine(SettingsStore.Folder, "voices", "tars-ref.wav");

    /// <summary>Whether the voice engine is installed.</summary>
    public static bool IsInstalled => File.Exists(VenvPython) && File.Exists(Path.Combine(SidecarDir, "server.py"));

    /// <summary>The reference clip in use.</summary>
    public string ReferencePath => string.IsNullOrWhiteSpace(_settings().Tts.ReferenceClip) ? DefaultReference : _settings().Tts.ReferenceClip;

    /// <summary>Current state.</summary>
    public VoiceState State { get; private set; } = VoiceState.Starting;

    /// <summary>Engine, device and VRAM, for the VOICE tab.</summary>
    public string Detail { get; private set; } = "";

    /// <summary>Whether the sidecar answered its last health check.</summary>
    public bool Healthy { get; private set; }

    private string BaseUrl => _settings().Tts.SidecarUrl.TrimEnd('/');

    #endregion

    #region Events

    /// <summary><see cref="State"/> or <see cref="Detail"/> changed.</summary>
    public event Action? StateChanged;

    /// <summary>A line for the scrollback (lines starting with "  ·" are details-mode only).</summary>
    public event Action<string>? Note;

    #endregion

    #region Public Methods

    /// <summary>Starts health checks (and with them the sidecar).</summary>
    public async void Start()
    {
        _healthTimer.Start();
        await TickAsync();
    }

    /// <summary>
    /// Changes the GPU engine without restarting anything. After 1.2 s of no further changes (so cycling through the
    /// list doesn't load each one) we load the new engine; the sidecar drops the other GPU engine. Kokoro covers
    /// replies while it loads, and speech recognition is untouched.
    /// </summary>
    public async void SwitchEngine(string engine)
    {
        _switchCts?.Cancel();
        var cts = _switchCts = new CancellationTokenSource();
        try { await Task.Delay(1200, cts.Token); }
        catch (OperationCanceledException) { return; }

        _loading = [engine];
        UpdateState();
        Note?.Invoke($"voice: loading {engine}…");
        if (engine == "kokoro") await PostAsync("/unload");     // the CPU voice gives the GPU back
        await PostAsync($"/load?model={engine}", TimeSpan.FromMinutes(3));
        await TickAsync();
        if (!cts.IsCancellationRequested) Note?.Invoke($"voice: {engine} ready");
    }

    /// <summary>Kills and relaunches the sidecar (only needed after installing or repairing the voice engine).</summary>
    public void RestartSidecar()
    {
        try { _proc?.Kill(true); }
        catch (InvalidOperationException) { }
        _proc = null;
        _startFailures = 0;
        _nextStartAttempt = DateTime.MinValue;
        _ = TickAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _healthTimer.Stop();
        try { _proc?.Kill(true); }
        catch (InvalidOperationException) { }
        _sapi?.Dispose();
    }

    #endregion

    #region Private Methods

    private async Task TickAsync()
    {
        var health = await GetHealthAsync();
        if (health is not { } h)
        {
            Healthy = false;
            _loaded = _loading = [];
            if (IsInstalled && DateTime.UtcNow >= _nextStartAttempt) StartProcess();
            UpdateState();
            return;
        }

        Healthy = true;
        _startFailures = 0;
        _loaded = Names(h, "loaded");
        _loading = Names(h, "loading");
        _vramMb = h.TryGetProperty("vram_mb", out var v) ? v.GetDouble() : 0;
        await EnsureReferenceAsync();
        await IdleUnloadAsync();
        UpdateState();
    }

    private static string[] Names(JsonElement h, string key) =>
        h.TryGetProperty(key, out var list) ? list.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];

    private void UpdateState()
    {
        var engine = _settings().Tts.Engine;
        bool warming = _loading.Length > 0 && (_loading.Contains(engine) || !_loaded.Contains(engine));
        var state = (installed: IsInstalled || Healthy, Healthy) switch
        {
            (false, _) => VoiceState.NotInstalled,
            (_, false) => _proc is { HasExited: false } ? VoiceState.Starting : VoiceState.Offline,
            _ when _game.Active => VoiceState.GameMode,
            _ when warming => VoiceState.Warming,
            _ => VoiceState.Online,
        };
        var detail = state switch
        {
            VoiceState.GameMode => "kokoro · cpu",
            VoiceState.Online or VoiceState.Warming => _loaded.Contains(engine) ? $"{engine} · cuda · {_vramMb / 1024:0.0} GB" : $"{engine} · unloaded",
            VoiceState.NotInstalled => "run [INSTALL]",
            _ => "",
        };
        if (state == State && detail == Detail) return;
        State = state;
        Detail = detail;
        StateChanged?.Invoke();
    }

    private async Task<JsonElement?> GetHealthAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(1500);
            return await _http.GetFromJsonAsync<JsonElement>($"{BaseUrl}/health", cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    private void StartProcess()
    {
        if (_proc is { HasExited: false }) return;
        try
        {
            var engine = _settings().Tts.Engine;
            Directory.CreateDirectory(SettingsStore.Folder);
            var log = new StreamWriter(TtsLog, append: true) { AutoFlush = true };
            log.WriteLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} starting sidecar ({engine})");

            var proc = new Process { StartInfo = SidecarStartInfo(engine), EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => AppendLog(log, e.Data);
            proc.ErrorDataReceived += (_, e) => AppendLog(log, e.Data);
            proc.Exited += (_, _) => OnSidecarExited(proc, log);
            proc.Start();
            Win32.AssignProcessToJobObject(_job, proc.Handle);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _proc = proc;
            _nextStartAttempt = DateTime.UtcNow.AddSeconds(30);
            Log.Write("voice: sidecar started");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Log.Write($"voice: sidecar start failed: {ex.Message}");
            _nextStartAttempt = DateTime.UtcNow.AddMinutes(1);
            return;
        }

        try { _proc.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* it already exited: the backoff handles it */ }
    }

    private ProcessStartInfo SidecarStartInfo(string engine)
    {
        var psi = new ProcessStartInfo(VenvPython)
        {
            ArgumentList = { "-u", Path.Combine(SidecarDir, "server.py"), "--port", new Uri(BaseUrl).Port.ToString(), "--engine", engine },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = SidecarDir,
        };
        if (File.Exists(ReferencePath))
        {
            psi.ArgumentList.Add("--ref");
            psi.ArgumentList.Add(ReferencePath);
        }
        if (_game.Active) psi.ArgumentList.Add("--no-warm");     // start cold in a game: the GPU stays free
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["TQDM_DISABLE"] = "1";
        psi.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        psi.Environment["HF_HUB_OFFLINE"] = "1";                  // the weights come with the install; we never phone home
        return psi;
    }

    private static void AppendLog(StreamWriter log, string? line)
    {
        if (line == null) return;
        try
        {
            lock (log) log.WriteLine(line);
        }
        catch (ObjectDisposedException) { /* output arriving after exit */ }
    }

    private void OnSidecarExited(Process proc, StreamWriter log)
    {
        lock (log)
        {
            log.WriteLine($"==== exited ({proc.ExitCode})");
            log.Dispose();
        }
        _startFailures++;
        // Crash loops back off: 5 s, 10 s, 20 s … 5 min.
        _nextStartAttempt = DateTime.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(_startFailures, 6))));
    }

    private async Task EnsureReferenceAsync()
    {
        if (_makingReference || !string.IsNullOrWhiteSpace(_settings().Tts.ReferenceClip) || File.Exists(DefaultReference)) return;
        _makingReference = true;
        try
        {
            Note?.Invoke("voice: rendering the default reference clip (Kokoro, local)…");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var response = await _http.PostAsync($"{BaseUrl}/make-reference?path={Uri.EscapeDataString(DefaultReference)}", null, cts.Token);
            response.EnsureSuccessStatusCode();
            Note?.Invoke("voice: reference clip ready");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            Log.Write($"voice: reference failed: {ex.Message}");
        }
        finally { _makingReference = false; }
    }

    private async Task IdleUnloadAsync()
    {
        int minutes = _settings().Tts.IdleUnloadMinutes;
        if (minutes <= 0 || !_loaded.Any(e => e is "turbo" or "chatterbox")) return;
        if (DateTime.UtcNow - _lastSay < TimeSpan.FromMinutes(minutes)) return;

        await PostAsync("/unload");
        // Kokoro covers the next reply while the GPU voice reloads, so we have it warm rather than cold (CPU, small).
        await PostAsync("/load?model=kokoro", TimeSpan.FromMinutes(1));
        Log.Write($"voice: GPU model unloaded after {minutes} idle minutes (kokoro warm for the next reply)");
    }

    private async void OnGameModeChanged()
    {
        if (_game.Active)
        {
            Note?.Invoke($"voice: game mode ({_game.Reason}) → Kokoro on CPU, GPU freed");
            await PostAsync("/unload");
        }
        else
        {
            Note?.Invoke("voice: game closed → GPU voice");
            _ = PostAsync($"/load?model={_settings().Tts.Engine}", TimeSpan.FromMinutes(2));
        }
        UpdateState();
    }

    /// <summary>POST to the sidecar. Failures are fine: the next health check tells the truth.</summary>
    private async Task PostAsync(string path, TimeSpan? timeout = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
            using var _ = await _http.PostAsync(BaseUrl + path, null, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
    }

    #endregion
}
