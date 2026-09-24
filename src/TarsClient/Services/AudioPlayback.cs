using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TarsClient.Services;

/// <summary>
/// One output stream, always open: a gapless 24 kHz mono queue (local voice, WAVs, the chime) at 0–150 % volume with
/// a soft clip above 100 %, resampled to the device's mix format. <see cref="PlaybackStarted"/> and
/// <see cref="PlaybackEnded"/> (queue empty for 250 ms) are raised on the UI thread.
/// </summary>
public sealed class AudioPlayback : IDisposable
{
    #region Fields

    /// <summary>The queue's sample rate.</summary>
    public const int Rate = 24000;

    private const int EndAfterMs = 250;

    private readonly SpeechQueue _queue = new();
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _watch;
    private WasapiOut? _out;
    private string _deviceId = "";
    private bool _playing;

    #endregion

    #region Constructor

    /// <summary>Creates the player; playback events are raised on <paramref name="ui"/>.</summary>
    public AudioPlayback(Dispatcher ui)
    {
        _ui = ui;
        _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _watch.Tick += (_, _) => CheckEnded();
    }

    #endregion

    #region Properties

    /// <summary>Whether anything is queued or audible.</summary>
    public bool IsPlaying => _playing;

    /// <summary>When the last audible sample went to the device (UTC ticks), for the echo tail.</summary>
    public long LastAudioTicks => _queue.LastAudioTicks;

    /// <summary>Output volume, 0–1.5.</summary>
    public double Volume
    {
        get => _queue.Volume;
        set => _queue.Volume = Math.Clamp(value, 0, 1.5);
    }

    #endregion

    #region Events

    /// <summary>The first queued audio started.</summary>
    public event Action? PlaybackStarted;

    /// <summary>The queue has been empty for 250 ms, or we stopped.</summary>
    public event Action? PlaybackEnded;

    #endregion

    #region Public Methods

    /// <summary>Opens <paramref name="deviceId"/> (empty means the Windows default).</summary>
    public void Open(string deviceId)
    {
        _deviceId = deviceId;
        Reopen();
    }

    /// <summary>(Re)creates the output on the chosen or default device, e.g. after a headset replug.</summary>
    public void Reopen()
    {
        try
        {
            _out?.Dispose();
            _out = null;
            using var enumerator = new MMDeviceEnumerator();
            var device = AudioDevices.Find(enumerator, DataFlow.Render, _deviceId)
                         ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mix = device.AudioClient.MixFormat;
            ISampleProvider chain = _queue;
            if (mix.SampleRate != Rate) chain = new WdlResamplingSampleProvider(chain, mix.SampleRate);
            chain = new ChannelFan(chain, mix.Channels);

            _out = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
            _out.PlaybackStopped += OnPlaybackStopped;
            _out.Init(chain);
            _out.Play();
            Log.Write($"playback: {device.FriendlyName} {mix.SampleRate} Hz x{mix.Channels}");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Log.Write($"playback: open failed: {ex.Message}");
            _ui.BeginInvoke(async () =>
            {
                await Task.Delay(2000);
                if (_out == null) Reopen();
            });
        }
    }

    /// <summary>Queues a complete WAV (any PCM rate and channel count).</summary>
    public void EnqueueWav(byte[] wav)
    {
        try { Enqueue(DecodeWav(wav)); }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException)
        {
            Log.Write($"playback: bad wav: {ex.Message}");
        }
    }

    /// <summary>Queues 24 kHz mono samples (the local voice calls this once per streamed chunk).</summary>
    public void Enqueue(float[] samples)
    {
        if (samples.Length == 0) return;
        _queue.Add(samples);
        if (!_playing)
        {
            _playing = true;
            PlaybackStarted?.Invoke();
        }
        _watch.Start();
    }

    /// <summary>Queues the alarm chime.</summary>
    public void Chime() => Enqueue(ChimeSamples());

    /// <summary>Clears the queue and goes silent immediately.</summary>
    public void Stop()
    {
        _queue.Clear();
        CheckEnded(force: true);
    }

    /// <inheritdoc />
    public void Dispose() => _out?.Dispose();

    #endregion

    #region Internal Methods

    /// <summary>Three sine blips: 880, 880 and 1175 Hz, 180 ms each, 220 ms apart, peak 0.5.</summary>
    internal static float[] ChimeSamples()
    {
        int step = (int)(Rate * 0.22), length = (int)(Rate * 0.18);
        var samples = new float[step * 2 + length];
        double[] freqs = [880, 880, 1175];
        for (int blip = 0; blip < freqs.Length; blip++)
        {
            for (int i = 0; i < length; i++)
            {
                double envelope = Math.Min(1, i / (Rate * 0.01)) * Math.Min(1, (length - i) / (Rate * 0.04));
                samples[blip * step + i] = (float)(0.5 * envelope * Math.Sin(2 * Math.PI * freqs[blip] * i / Rate));
            }
        }
        return samples;
    }

    /// <summary>Any PCM WAV → 24 kHz mono float.</summary>
    internal static float[] DecodeWav(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        ISampleProvider provider = reader.ToSampleProvider();
        if (provider.WaveFormat.Channels == 2) provider = new StereoToMonoSampleProvider(provider);
        if (provider.WaveFormat.SampleRate != Rate) provider = new WdlResamplingSampleProvider(provider, Rate);

        var all = new List<float>((int)(reader.SampleCount + 1024));
        var buffer = new float[4096];
        int n;
        while ((n = provider.Read(buffer, 0, buffer.Length)) > 0) all.AddRange(buffer.AsSpan(0, n));
        return [.. all];
    }

    /// <summary>Linear up to 0.8, then a tanh knee that never exceeds 1.0, so 150 % volume doesn't crackle.</summary>
    internal static float SoftClip(float x)
    {
        float a = Math.Abs(x);
        if (a <= 0.8f) return x;
        return Math.Sign(x) * (0.8f + 0.2f * MathF.Tanh((a - 0.8f) / 0.2f));
    }

    #endregion

    #region Private Methods

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null) return;
        Log.Write($"playback stopped: {e.Exception.Message}; reopening");
        _ui.BeginInvoke(async () =>
        {
            await Task.Delay(800);
            Reopen();
        });
    }

    private void CheckEnded(bool force = false)
    {
        if (!_playing)
        {
            _watch.Stop();
            return;
        }
        var idleMs = (DateTime.UtcNow.Ticks - LastAudioTicks) / TimeSpan.TicksPerMillisecond;
        if (!force && (!_queue.IsEmpty || idleMs < EndAfterMs)) return;

        _playing = false;
        _watch.Stop();
        PlaybackEnded?.Invoke();
    }

    #endregion

    #region Nested Types

    /// <summary>The queue itself. It reads silence when empty, so the device stream never stops.</summary>
    private sealed class SpeechQueue : ISampleProvider
    {
        private readonly Queue<float[]> _chunks = new();
        private float[]? _current;
        private int _pos;
        private long _lastAudioTicks;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

        public double Volume { get; set; } = 1.0;

        public long LastAudioTicks => Interlocked.Read(ref _lastAudioTicks);

        public bool IsEmpty
        {
            get { lock (_chunks) return _current == null && _chunks.Count == 0; }
        }

        public void Add(float[] samples)
        {
            lock (_chunks) _chunks.Enqueue(samples);
        }

        public void Clear()
        {
            lock (_chunks)
            {
                _chunks.Clear();
                _current = null;
                _pos = 0;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int written = 0;
            float volume = (float)Volume;
            lock (_chunks)
            {
                while (written < count)
                {
                    if (_current == null)
                    {
                        if (_chunks.Count == 0) break;
                        _current = _chunks.Dequeue();
                        _pos = 0;
                    }
                    int n = Math.Min(count - written, _current.Length - _pos);
                    for (int i = 0; i < n; i++)
                    {
                        float x = _current[_pos + i] * volume;
                        buffer[offset + written + i] = volume > 1f ? SoftClip(x) : x;
                    }
                    written += n;
                    _pos += n;
                    if (_pos >= _current.Length) _current = null;
                }
            }
            if (written > 0) Interlocked.Exchange(ref _lastAudioTicks, DateTime.UtcNow.Ticks);
            Array.Clear(buffer, offset + written, count - written);
            return count;
        }
    }

    /// <summary>Mono to N device channels.</summary>
    private sealed class ChannelFan(ISampleProvider source, int channels) : ISampleProvider
    {
        private float[] _mono = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / channels;
            if (_mono.Length < frames) _mono = new float[frames];
            int got = source.Read(_mono, 0, frames);
            for (int f = 0; f < got; f++)
            {
                for (int c = 0; c < channels; c++) buffer[offset + f * channels + c] = _mono[f];
            }
            return got * channels;
        }
    }

    #endregion
}
