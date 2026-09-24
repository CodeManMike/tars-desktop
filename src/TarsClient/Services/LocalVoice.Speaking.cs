using System.Diagnostics;
using System.Net.Http.Json;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;

namespace TarsClient.Services;

/// <summary>
/// The speaking half of <see cref="LocalVoice"/>: we voice <c>say</c> lines strictly in arrival order, streaming PCM
/// through TARS FX into the playback queue. The GPU engine normally speaks; Kokoro (CPU) covers game mode, warming and
/// a GPU engine that's late; the built-in Windows voice covers a dead sidecar. A reply is never left unspoken, and
/// nothing is voiced by the server.
/// </summary>
public sealed partial class LocalVoice
{
    #region Fields

    private static readonly TimeSpan ResidentFirstAudio = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ColdKokoroFirstAudio = TimeSpan.FromSeconds(12);

    private readonly SemaphoreSlim _speakGate = new(1, 1);
    private readonly Dictionary<string, float[]> _fillerCache = [];
    private readonly object _sapiGate = new();
    private CancellationTokenSource _stopCts = new();
    private SpeechSynthesizer? _sapi;

    #endregion

    #region Public Methods

    /// <summary>Voices one <c>say</c> chunk. Calls queue up and play in arrival order.</summary>
    /// <param name="speak">The text, already normalised for speech by the server.</param>
    /// <param name="kind"><c>reply</c>, <c>filler</c> (short, cached) or <c>alarm</c>.</param>
    public async void Say(string speak, string kind)
    {
        if (string.IsNullOrWhiteSpace(speak)) return;
        _lastSay = DateTime.UtcNow;
        var token = _stopCts.Token;
        await _speakGate.WaitAsync();
        try
        {
            if (!token.IsCancellationRequested) await SpeakOneAsync(speak, kind, token);
        }
        finally { _speakGate.Release(); }
    }

    /// <summary>Drops everything queued or in flight (PTT, Esc, a server stop).</summary>
    public void Stop()
    {
        var old = _stopCts;
        _stopCts = new CancellationTokenSource();
        old.Cancel();
        _sapi?.SpeakAsyncCancelAll();
    }

    /// <summary><c>POST /v1/audio/speech</c> (streamed PCM) → TARS FX → playback, optionally keeping the samples.</summary>
    public async Task<SpeechResult> StreamAsync(string engine, string text, double speed, CancellationToken stop,
                                                TimeSpan firstAudioTimeout, bool collect, bool play = true)
    {
        var tts = _settings().Tts;
        var clock = Stopwatch.StartNew();
        bool started = false;
        long firstMs = 0;
        var all = collect ? new List<float>() : null;
        var fx = tts.Fx ? new TarsFx(tts.FxPitch, tts.FxRing) : null;
        var decoder = new Pcm16Decoder();
        using var firstCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        firstCts.CancelAfter(firstAudioTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/audio/speech")
            {
                Content = JsonContent.Create(SpeechRequest(engine, text, speed, tts)),
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, firstCts.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(firstCts.Token);
            var buffer = new byte[9600];
            int n;
            while ((n = await stream.ReadAsync(buffer, started ? stop : firstCts.Token)) > 0)
            {
                var samples = decoder.Decode(buffer.AsSpan(0, n));
                var chunk = fx != null ? fx.Process(samples) : samples;
                if (!started)
                {
                    started = true;
                    firstMs = clock.ElapsedMilliseconds;
                }
                if (stop.IsCancellationRequested) return new(false, true, firstMs, null);
                if (play) await _ui.InvokeAsync(() => _playback.Enqueue(chunk));
                all?.AddRange(chunk);
            }
            return new(started, started, firstMs, all?.ToArray());
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            if (!stop.IsCancellationRequested) Log.Write($"voice: {engine} failed after {clock.ElapsedMilliseconds} ms: {ex.Message}");
            return new(false, started, 0, null);
        }
    }

    #endregion

    #region Private Methods

    private object SpeechRequest(string engine, string text, double speed, TtsSettings tts) => new
    {
        model = engine,
        input = text,
        voice = File.Exists(ReferencePath) ? ReferencePath : "default",
        response_format = "pcm",
        speed = speed * tts.Speed,
        exaggeration = tts.Exaggeration,
        pace = tts.Pace,
        temperature = tts.Temperature,
    };

    /// <summary>The engine for this line. A GPU engine that isn't loaded gets loaded in the background while Kokoro speaks.</summary>
    private string PickEngine()
    {
        var engine = _settings().Tts.Engine;
        if (_game.Active) return "kokoro";
        if (_loaded.Contains(engine)) return engine;

        if (!_loading.Contains(engine))
        {
            _loading = [engine];
            UpdateState();
            _ = PostAsync($"/load?model={engine}", TimeSpan.FromMinutes(2)).ContinueWith(_ => _ui.BeginInvoke(TickAsync));
        }
        return "kokoro";
    }

    private TimeSpan FirstAudioBudget(string engine) =>
        engine == "kokoro" && !_loaded.Contains("kokoro") ? ColdKokoroFirstAudio : ResidentFirstAudio;

    private async Task SpeakOneAsync(string text, string kind, CancellationToken stop)
    {
        if (!Healthy)
        {
            await Task.Run(() => SpeakWindows(text, stop));
            return;
        }

        bool filler = kind == "filler";
        var engine = PickEngine();
        var tts = _settings().Tts;
        var cacheKey = $"{engine}|{text}|{tts.Speed}|{tts.FxPitch}|{tts.FxRing}|{tts.Fx}";
        if (filler && _fillerCache.TryGetValue(cacheKey, out var cached))
        {
            _playback.Enqueue(cached);
            return;
        }

        var result = await StreamAsync(engine, text, filler ? 1.1 : 1.0, stop, FirstAudioBudget(engine), collect: filler);
        if (result.Ok)
        {
            if (filler && result.Samples != null) _fillerCache[cacheKey] = result.Samples;
            Note?.Invoke($"  · voice {engine} {result.FirstAudioMs} ms");
            return;
        }
        if (stop.IsCancellationRequested || result.Started) return;

        // A GPU engine that's late or failing: Kokoro takes this line.
        if (engine != "kokoro")
        {
            Note?.Invoke($"  · voice {engine}: no audio in 3 s, using kokoro");
            result = await StreamAsync("kokoro", text, 1.0, stop, FirstAudioBudget("kokoro"), collect: false);
            if (result.Ok || result.Started || stop.IsCancellationRequested) return;
        }
        await Task.Run(() => SpeakWindows(text, stop));
    }

    /// <summary>The last resort when the sidecar is down: the built-in Windows voice, still local, still through TARS FX.</summary>
    private void SpeakWindows(string text, CancellationToken stop)
    {
        if (stop.IsCancellationRequested) return;
        lock (_sapiGate)
        {
            try
            {
                _sapi ??= CreateWindowsVoice();
                using var ms = new MemoryStream();
                _sapi.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(AudioPlayback.Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                _sapi.Speak(text);
                _sapi.SetOutputToNull();
                if (stop.IsCancellationRequested) return;

                var samples = new Pcm16Decoder().Decode(ms.ToArray());
                var tts = _settings().Tts;
                var output = tts.Fx ? new TarsFx(tts.FxPitch, tts.FxRing).Process(samples) : samples;
                _ui.Invoke(() => _playback.Enqueue(output));
                Note?.Invoke("  · voice: windows (sidecar offline)");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or PlatformNotSupportedException)
            {
                Log.Write($"voice: windows voice failed: {ex.Message}");
            }
        }
    }

    private static SpeechSynthesizer CreateWindowsVoice()
    {
        var synth = new SpeechSynthesizer { Rate = -1 };
        try { synth.SelectVoiceByHints(VoiceGender.Male, VoiceAge.Adult); }
        catch (InvalidOperationException) { /* no male voice installed: the default will do */ }
        return synth;
    }

    #endregion
}
