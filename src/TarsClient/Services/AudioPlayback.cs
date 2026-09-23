using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TarsClient.Services;

/// <summary>
/// One output stream, always open: a gapless 24 kHz mono queue (server WAVs, local voice PCM, chime),
/// volume 0–150 % with a soft clip above 100 %, resampled to the device mix format.
/// PlaybackStarted / PlaybackEnded (queue empty for 250 ms) are raised on the UI thread.
/// </summary>
public sealed class AudioPlayback : IDisposable
{
    public const int Rate = 24000;

    readonly SpeechQueue _queue = new();
    readonly System.Windows.Threading.Dispatcher _ui;
    readonly System.Windows.Threading.DispatcherTimer _watch;
    WasapiOut? _out;
    string _deviceId = "";
    bool _playing;

    public event Action? PlaybackStarted;
    public event Action? PlaybackEnded;

    public bool IsPlaying => _playing;
    /// <summary>When the last audible sample was handed to the device (UTC ticks), for the echo tail.</summary>
    public long LastAudioTicks => Interlocked.Read(ref _queue.LastAudioTicks);
    public double Volume { get => _queue.Volume; set => _queue.Volume = Math.Clamp(value, 0, 1.5); }

    public AudioPlayback(System.Windows.Threading.Dispatcher ui)
    {
        _ui = ui;
        _watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _watch.Tick += (_, _) => CheckEnded();
    }

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
            using var en = new MMDeviceEnumerator();
            var dev = AudioDevices.Find(en, DataFlow.Render, _deviceId) ?? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mix = dev.AudioClient.MixFormat;
            ISampleProvider chain = _queue;
            if (mix.SampleRate != Rate) chain = new WdlResamplingSampleProvider(chain, mix.SampleRate);
            chain = new ChannelFan(chain, mix.Channels);
            _out = new WasapiOut(dev, AudioClientShareMode.Shared, true, 60);
            _out.PlaybackStopped += (_, e) =>
            {
                if (e.Exception != null)
                {
                    Log.Write($"playback stopped: {e.Exception.Message}; reopening");
                    _ui.BeginInvoke(async () => { await Task.Delay(800); Reopen(); });
                }
            };
            _out.Init(chain);
            _out.Play();
            Log.Write($"playback: {dev.FriendlyName} {mix.SampleRate} Hz x{mix.Channels}");
        }
        catch (Exception ex)
        {
            Log.Write($"playback: open failed: {ex.Message}");
            _ui.BeginInvoke(async () => { await Task.Delay(2000); if (_out == null) Reopen(); });
        }
    }

    /// <summary>Queue a complete WAV (server voice). Any PCM rate/channels are converted to 24 kHz mono.</summary>
    public void EnqueueWav(byte[] wav)
    {
        try
        {
            using var reader = new WaveFileReader(new MemoryStream(wav));
            ISampleProvider sp = reader.ToSampleProvider();
            if (sp.WaveFormat.Channels == 2) sp = new StereoToMonoSampleProvider(sp);
            if (sp.WaveFormat.SampleRate != Rate) sp = new WdlResamplingSampleProvider(sp, Rate);
            var all = new List<float>((int)(reader.SampleCount + 1024));
            var buf = new float[4096];
            int n;
            while ((n = sp.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.AsSpan(0, n));
            Enqueue(all.ToArray());
        }
        catch (Exception ex) { Log.Write($"playback: bad wav: {ex.Message}"); }
    }

    /// <summary>Queue 24 kHz mono float samples (local voice streams call this per chunk).</summary>
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

    /// <summary>Three sine blips: 880, 880, 1175 Hz, 180 ms each, 220 ms apart, peak 0.5.</summary>
    public void Chime()
    {
        int step = (int)(Rate * 0.22), len = (int)(Rate * 0.18);
        var s = new float[step * 2 + len];
        double[] f = [880, 880, 1175];
        for (int b = 0; b < 3; b++)
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / Rate;
                double env = Math.Min(1, i / (Rate * 0.01)) * Math.Min(1, (len - i) / (Rate * 0.04));
                s[b * step + i] = (float)(0.5 * env * Math.Sin(2 * Math.PI * f[b] * t));
            }
        Enqueue(s);
    }

    /// <summary>Clear the queue and silence immediately.</summary>
    public void Stop()
    {
        _queue.Clear();
        CheckEnded(force: true);
    }

    void CheckEnded(bool force = false)
    {
        if (!_playing) { _watch.Stop(); return; }
        var idleMs = (DateTime.UtcNow.Ticks - LastAudioTicks) / TimeSpan.TicksPerMillisecond;
        if (force || (_queue.IsEmpty && idleMs >= 250))
        {
            _playing = false;
            _watch.Stop();
            PlaybackEnded?.Invoke();
        }
    }

    public void Dispose() => _out?.Dispose();

    /// <summary>The queue itself: reads silence when empty so the device stream never stops.</summary>
    sealed class SpeechQueue : ISampleProvider
    {
        readonly Queue<float[]> _q = new();
        float[]? _cur;
        int _pos;
        public long LastAudioTicks;
        public double Volume = 1.0;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

        public bool IsEmpty { get { lock (_q) return _cur == null && _q.Count == 0; } }

        public void Add(float[] s) { lock (_q) _q.Enqueue(s); }

        public void Clear() { lock (_q) { _q.Clear(); _cur = null; _pos = 0; } }

        public int Read(float[] buffer, int offset, int count)
        {
            int written = 0;
            float vol = (float)Volume;
            lock (_q)
            {
                while (written < count)
                {
                    if (_cur == null)
                    {
                        if (_q.Count == 0) break;
                        _cur = _q.Dequeue();
                        _pos = 0;
                    }
                    int n = Math.Min(count - written, _cur.Length - _pos);
                    for (int i = 0; i < n; i++)
                    {
                        float x = _cur[_pos + i] * vol;
                        if (vol > 1f) x = SoftClip(x);
                        buffer[offset + written + i] = x;
                    }
                    written += n;
                    _pos += n;
                    if (_pos >= _cur.Length) _cur = null;
                }
            }
            if (written > 0) Interlocked.Exchange(ref LastAudioTicks, DateTime.UtcNow.Ticks);
            Array.Clear(buffer, offset + written, count - written);
            return count;
        }

        /// <summary>Linear up to 0.8, then a tanh knee that never exceeds 1.0 (no crackle at 150 %).</summary>
        static float SoftClip(float x)
        {
            float a = Math.Abs(x);
            if (a <= 0.8f) return x;
            return Math.Sign(x) * (0.8f + 0.2f * MathF.Tanh((a - 0.8f) / 0.2f));
        }
    }

    /// <summary>Mono to N device channels.</summary>
    sealed class ChannelFan(ISampleProvider src, int channels) : ISampleProvider
    {
        float[] _mono = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / channels;
            if (_mono.Length < frames) _mono = new float[frames];
            int got = src.Read(_mono, 0, frames);
            for (int f = 0; f < got; f++)
                for (int c = 0; c < channels; c++)
                    buffer[offset + f * channels + c] = _mono[f];
            return got * channels;
        }
    }
}
