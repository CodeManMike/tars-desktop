using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text.Json;
using TarsClient.Models;
using TarsClient.Native;

namespace TarsClient.Services;

public enum VoiceState { NotInstalled, Starting, Online, Warming, GameMode, Offline }

/// <summary>
/// TARS's voice, generated entirely on this PC (spec section 6, minus the server fallback):
///  - starts/stops the Python sidecar (hidden, in a kill-on-close job), health-checks it every 5 s;
///  - voices `say` lines in arrival order, streaming PCM into the playback queue through TARS FX;
///  - GPU engine (Chatterbox Turbo) normally; Kokoro on CPU while a game runs or while the GPU model warms;
///  - no audio within 3 s → Kokoro; sidecar dead → the built-in Windows voice. A reply is never left unspoken;
///  - unloads the GPU model after N idle minutes.
/// </summary>
public sealed class LocalVoice : IDisposable
{
    public static string VenvPython { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TARS", "tts-venv", "Scripts", "python.exe");
    public static string SidecarDir { get; } = Path.Combine(AppContext.BaseDirectory, "tts-sidecar");
    public static string DefaultReference { get; } = Path.Combine(SettingsStore.Folder, "voices", "tars-ref.wav");
    static readonly string TtsLog = Path.Combine(SettingsStore.Folder, "tts.log");

    readonly Func<ClientSettings> _settings;
    readonly AudioPlayback _playback;
    readonly GameMode _game;
    readonly System.Windows.Threading.Dispatcher _ui;
    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly SemaphoreSlim _speakGate = new(1, 1);
    readonly Dictionary<string, float[]> _fillerCache = new();
    readonly IntPtr _job = Win32.CreateKillOnCloseJob();
    readonly System.Windows.Threading.DispatcherTimer _healthTimer;
    Process? _proc;
    CancellationTokenSource _stopCts = new();
    DateTime _lastSay = DateTime.UtcNow;
    DateTime _nextStartAttempt = DateTime.MinValue;
    int _startFailures;
    bool _makingReference;
    string[] _loaded = [];
    string[] _loading = [];
    SpeechSynthesizer? _sapi;
    readonly object _sapiGate = new();

    public VoiceState State { get; private set; } = VoiceState.Starting;
    public string Detail { get; private set; } = "";
    public bool Healthy { get; private set; }
    public double VramMb { get; private set; }
    public event Action? StateChanged;
    /// <summary>A line of interest for the scrollback in details mode.</summary>
    public event Action<string>? Note;

    string BaseUrl => _settings().Tts.SidecarUrl.TrimEnd('/');
    public static bool IsInstalled => File.Exists(VenvPython) && File.Exists(Path.Combine(SidecarDir, "server.py"));
    public string ReferencePath => string.IsNullOrWhiteSpace(_settings().Tts.ReferenceClip) ? DefaultReference : _settings().Tts.ReferenceClip;

    public LocalVoice(System.Windows.Threading.Dispatcher ui, Func<ClientSettings> settings, AudioPlayback playback, GameMode game)
    {
        _ui = ui;
        _settings = settings;
        _playback = playback;
        _game = game;
        _game.Changed += OnGameModeChanged;
        _healthTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _healthTimer.Tick += async (_, _) => await TickAsync();
    }

    public async void Start()
    {
        _healthTimer.Start();
        await TickAsync();
    }

    // ------------------------------------------------------------------ lifecycle / health

    async Task TickAsync()
    {
        var health = await GetHealthAsync();
        if (health is { } h)
        {
            Healthy = true;
            _startFailures = 0;
            _loaded = h.TryGetProperty("loaded", out var l) ? l.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
            _loading = h.TryGetProperty("loading", out var lg) ? lg.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
            VramMb = h.TryGetProperty("vram_mb", out var v) ? v.GetDouble() : 0;
            await EnsureReferenceAsync();
            await IdleUnloadAsync();
        }
        else
        {
            Healthy = false;
            _loaded = _loading = [];
            if (IsInstalled && DateTime.UtcNow >= _nextStartAttempt) StartProcess();
        }
        UpdateState();
    }

    void UpdateState()
    {
        var engine = _settings().Tts.Engine;
        VoiceState s;
        if (!IsInstalled && !Healthy) s = VoiceState.NotInstalled;
        else if (!Healthy) s = _proc is { HasExited: false } ? VoiceState.Starting : VoiceState.Offline;
        else if (_game.Active) s = VoiceState.GameMode;
        else if (_loading.Contains(engine) || !_loaded.Contains(engine)) s = _loading.Length > 0 ? VoiceState.Warming : VoiceState.Online;
        else s = VoiceState.Online;

        var detail = s switch
        {
            VoiceState.GameMode => "kokoro · cpu",
            VoiceState.Online or VoiceState.Warming => _loaded.Contains(engine) ? $"{engine} · cuda · {VramMb / 1024:0.0} GB" : $"{engine} · unloaded",
            VoiceState.NotInstalled => "run [INSTALL]",
            _ => "",
        };
        if (s == State && detail == Detail) return;
        State = s;
        Detail = detail;
        StateChanged?.Invoke();
    }

    async Task<JsonElement?> GetHealthAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(1500);
            var doc = await _http.GetFromJsonAsync<JsonElement>($"{BaseUrl}/health", cts.Token);
            return doc;
        }
        catch { return null; }
    }

    void StartProcess()
    {
        if (_proc is { HasExited: false }) return;
        try
        {
            var s = _settings().Tts;
            var port = new Uri(BaseUrl).Port;
            var psi = new ProcessStartInfo(VenvPython)
            {
                ArgumentList = { "-u", Path.Combine(SidecarDir, "server.py"), "--port", port.ToString(), "--engine", s.Engine },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = SidecarDir,
            };
            if (File.Exists(ReferencePath)) { psi.ArgumentList.Add("--ref"); psi.ArgumentList.Add(ReferencePath); }
            // Start cold while a game runs: Kokoro loads on demand, the GPU stays free.
            if (_game.Active) psi.ArgumentList.Add("--no-warm");
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["TQDM_DISABLE"] = "1";
            psi.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
            psi.Environment["HF_HUB_OFFLINE"] = "1";          // weights come with the install; never phone home

            Directory.CreateDirectory(SettingsStore.Folder);
            var log = new StreamWriter(TtsLog, append: true) { AutoFlush = true };
            log.WriteLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} starting sidecar ({s.Engine})");
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) try { lock (log) log.WriteLine(e.Data); } catch { } };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) try { lock (log) log.WriteLine(e.Data); } catch { } };
            p.Exited += (_, _) =>
            {
                lock (log) { log.WriteLine($"==== exited ({p.ExitCode})"); log.Dispose(); }
                _startFailures++;
                // Crash loops back off: 5 s, 10 s, 20 s … 5 min.
                _nextStartAttempt = DateTime.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(_startFailures, 6))));
            };
            p.Start();
            Win32.AssignProcessToJobObject(_job, p.Handle);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            _proc = p;
            _nextStartAttempt = DateTime.UtcNow.AddSeconds(30);
            Log.Write("voice: sidecar started");
        }
        catch (Exception ex)
        {
            Log.Write($"voice: sidecar start failed: {ex.Message}");
            _nextStartAttempt = DateTime.UtcNow.AddMinutes(1);
        }
    }

    CancellationTokenSource? _switchCts;

    /// <summary>
    /// Change the GPU engine without restarting anything: after 1.2 s of no further changes (cycling through the
    /// list doesn't load each one), load the new engine; the sidecar drops the other GPU engine. Kokoro covers
    /// replies while it loads, and speech recognition is untouched.
    /// </summary>
    public async void SwitchEngine(string engine)
    {
        _switchCts?.Cancel();
        var cts = _switchCts = new CancellationTokenSource();
        try { await Task.Delay(1200, cts.Token); } catch (OperationCanceledException) { return; }
        _loading = [engine];
        UpdateState();
        Note?.Invoke($"voice: loading {engine}…");
        if (engine == "kokoro") await PostAsync("/unload");     // CPU voice: give the GPU back
        await PostAsync($"/load?model={engine}", TimeSpan.FromMinutes(3));
        await TickAsync();
        if (!cts.IsCancellationRequested) Note?.Invoke($"voice: {engine} ready");
    }

    /// <summary>Kill and relaunch (only after installing or repairing the voice engine).</summary>
    public void RestartSidecar()
    {
        try { _proc?.Kill(true); } catch { }
        _proc = null;
        _startFailures = 0;
        _nextStartAttempt = DateTime.MinValue;
        _ = TickAsync();
    }

    async Task EnsureReferenceAsync()
    {
        if (_makingReference || !string.IsNullOrWhiteSpace(_settings().Tts.ReferenceClip) || File.Exists(DefaultReference)) return;
        _makingReference = true;
        try
        {
            Note?.Invoke("voice: rendering the default reference clip (Kokoro, local)…");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var url = $"{BaseUrl}/make-reference?path={Uri.EscapeDataString(DefaultReference)}";
            var r = await _http.PostAsync(url, null, cts.Token);
            r.EnsureSuccessStatusCode();
            Note?.Invoke("voice: reference clip ready");
        }
        catch (Exception ex) { Log.Write($"voice: reference failed: {ex.Message}"); }
        finally { _makingReference = false; }
    }

    async Task IdleUnloadAsync()
    {
        int minutes = _settings().Tts.IdleUnloadMinutes;
        if (minutes <= 0 || !_loaded.Any(e => e is "turbo" or "chatterbox")) return;
        if (DateTime.UtcNow - _lastSay < TimeSpan.FromMinutes(minutes)) return;
        await PostAsync("/unload");
        Log.Write($"voice: GPU model unloaded after {minutes} idle minutes");
    }

    async void OnGameModeChanged()
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

    async Task PostAsync(string path, TimeSpan? timeout = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
            await _http.PostAsync(BaseUrl + path, null, cts.Token);
        }
        catch { }
    }

    // ------------------------------------------------------------------ speaking

    /// <summary>Voice one `say` chunk. Calls queue up and play strictly in arrival order.</summary>
    public async void Say(string speak, string kind)
    {
        if (string.IsNullOrWhiteSpace(speak)) return;
        _lastSay = DateTime.UtcNow;
        var token = _stopCts.Token;
        await _speakGate.WaitAsync();
        try
        {
            if (token.IsCancellationRequested) return;
            await SpeakOneAsync(speak, kind, token);
        }
        finally { _speakGate.Release(); }
    }

    /// <summary>Drop everything queued or in flight (PTT, Esc, server stop).</summary>
    public void Stop()
    {
        var old = _stopCts;
        _stopCts = new CancellationTokenSource();
        old.Cancel();
        _sapi?.SpeakAsyncCancelAll();
    }

    string PickEngine()
    {
        var engine = _settings().Tts.Engine;
        if (_game.Active) return "kokoro";
        if (!_loaded.Contains(engine))
        {
            // Warm the GPU model in the background; Kokoro covers this line so nobody waits.
            if (!_loading.Contains(engine))
            {
                _loading = [engine];
                UpdateState();
                _ = PostAsync($"/load?model={engine}", TimeSpan.FromMinutes(2)).ContinueWith(_ => _ui.BeginInvoke(TickAsync));
            }
            return "kokoro";
        }
        return engine;
    }

    async Task SpeakOneAsync(string text, string kind, CancellationToken stop)
    {
        var sw = Stopwatch.StartNew();
        bool filler = kind == "filler";
        var engine = Healthy ? PickEngine() : null;

        if (engine != null)
        {
            var key = $"{engine}|{text}|{_settings().Tts.Speed}|{_settings().Tts.FxPitch}|{_settings().Tts.FxRing}|{_settings().Tts.Fx}";
            if (filler && _fillerCache.TryGetValue(key, out var cached)) { _playback.Enqueue(cached); return; }

            var result = await StreamAsync(engine, text, filler ? 1.1 : 1.0, stop, TimeSpan.FromSeconds(3), collect: filler);
            if (result.Ok)
            {
                if (filler && result.Samples != null) _fillerCache[key] = result.Samples;
                Note?.Invoke($"  · voice {engine} {result.FirstAudioMs} ms");
                return;
            }
            if (stop.IsCancellationRequested) return;
            // Late or failed: Kokoro (CPU) if the GPU path was the problem.
            if (engine != "kokoro" && !result.Started)
            {
                Note?.Invoke($"  · voice {engine}: no audio in 3 s, using kokoro");
                result = await StreamAsync("kokoro", text, 1.0, stop, TimeSpan.FromSeconds(6), collect: false);
                if (result.Ok || result.Started || stop.IsCancellationRequested) return;
            }
            else if (result.Started) return;
        }
        if (!stop.IsCancellationRequested) await Task.Run(() => SpeakWindows(text, stop));
    }

    public sealed record StreamResult(bool Ok, bool Started, long FirstAudioMs, float[]? Samples);

    /// <summary>POST /v1/audio/speech (pcm stream) → TARS FX → playback. Optionally keeps the samples.</summary>
    public async Task<StreamResult> StreamAsync(string engine, string text, double speed, CancellationToken stop,
                                                TimeSpan firstAudioTimeout, bool collect, bool play = true)
    {
        var s = _settings().Tts;
        var sw = Stopwatch.StartNew();
        bool started = false;
        var all = collect ? new List<float>() : null;
        var fx = s.Fx ? new TarsFx(s.FxPitch, s.FxRing) : null;
        using var firstCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        firstCts.CancelAfter(firstAudioTimeout);
        try
        {
            var body = new
            {
                model = engine,
                input = text,
                voice = File.Exists(ReferencePath) ? ReferencePath : "default",
                response_format = "pcm",
                speed = speed * s.Speed,
                exaggeration = s.Exaggeration,
                pace = s.Pace,
                temperature = s.Temperature,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/audio/speech") { Content = JsonContent.Create(body) };
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, firstCts.Token);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(firstCts.Token);
            var buf = new byte[9600];
            int carry = -1;
            long firstMs = 0;
            while (true)
            {
                var token = started ? stop : firstCts.Token;
                int n = await stream.ReadAsync(buf, token);
                if (n == 0) break;
                // Int16 LE; a read can split a sample, so carry the odd byte over.
                var bytes = buf.AsSpan(0, n);
                var samples = new float[(n + (carry >= 0 ? 1 : 0)) / 2];
                int si = 0, bi = 0;
                if (carry >= 0) { samples[si++] = (short)(carry | bytes[0] << 8) / 32768f; bi = 1; carry = -1; }
                for (; bi + 1 < n; bi += 2) samples[si++] = (short)(bytes[bi] | bytes[bi + 1] << 8) / 32768f;
                if (bi < n) carry = bytes[bi];
                var outS = fx != null ? fx.Process(samples.AsSpan(0, si)) : samples[..si];
                if (!started) { started = true; firstMs = sw.ElapsedMilliseconds; }
                if (stop.IsCancellationRequested) return new(false, true, firstMs, null);
                if (play) await _ui.InvokeAsync(() => _playback.Enqueue(outS));
                all?.AddRange(outS);
            }
            return new(started, started, firstMs, all?.ToArray());
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            if (!stop.IsCancellationRequested) Log.Write($"voice: {engine} failed after {sw.ElapsedMilliseconds} ms: {ex.Message}");
            return new(false, started, 0, null);
        }
    }

    /// <summary>Last resort when the sidecar is down: the built-in Windows voice, still local, still through TARS FX.</summary>
    void SpeakWindows(string text, CancellationToken stop)
    {
        lock (_sapiGate)
        try
        {
            if (_sapi == null)
            {
                _sapi = new SpeechSynthesizer();
                try { _sapi.SelectVoiceByHints(VoiceGender.Male, VoiceAge.Adult); } catch { }
                _sapi.Rate = -1;
            }
            using var ms = new MemoryStream();
            _sapi.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(AudioPlayback.Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            _sapi.Speak(text);
            _sapi.SetOutputToNull();
            if (stop.IsCancellationRequested) return;
            var raw = ms.ToArray();
            var samples = new float[raw.Length / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(raw, i * 2) / 32768f;
            var t = _settings().Tts;
            var outS = t.Fx ? new TarsFx(t.FxPitch, t.FxRing).Process(samples) : samples;
            _ui.Invoke(() => _playback.Enqueue(outS));
            Note?.Invoke("  · voice: windows (sidecar offline)");
        }
        catch (Exception ex) { Log.Write($"voice: windows voice failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        _healthTimer.Stop();
        try { _proc?.Kill(true); } catch { }
        _sapi?.Dispose();
    }
}
