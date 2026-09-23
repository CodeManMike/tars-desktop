using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace TarsClient.Services;

/// <summary>
/// WASAPI capture of the chosen (or default) microphone, converted to 16 kHz mono Int16 LE
/// and emitted in 512-sample (1,024-byte) frames with an RMS level. Recovers from unplug/replug:
/// device events and a 2 s no-data watchdog both trigger a restart.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    public const int OutRate = 16000, FrameSamples = 512;

    readonly System.Windows.Threading.Dispatcher _ui;
    readonly System.Windows.Threading.DispatcherTimer _watchdog;
    WasapiCapture? _cap;
    WdlResampler? _rs;
    WaveFormat? _fmt;
    string _deviceId = "";
    float[] _mono = [];
    float[] _res = [];
    readonly short[] _frame = new short[FrameSamples];
    int _framePos;
    // one-shot diagnostics: levels in vs out over the first 5 s after (re)start
    long _diagIn, _diagOut; double _diagInSq, _diagOutSq; long _diagUntil;
    long _lastDataTicks;
    bool _wanted;

    /// <summary>Frame bytes and RMS (0..1). Raised on the capture thread: keep handlers cheap.</summary>
    public event Action<byte[], float>? Frame;
    /// <summary>Full-rate mono samples before resampling (for recording a reference clip). Capture thread.</summary>
    public event Action<float[], int>? RawSamples;
    /// <summary>true = Windows privacy settings blocked the mic. UI thread.</summary>
    public event Action<bool>? BlockedChanged;

    public bool IsBlocked { get; private set; }
    /// <summary>The endpoint is muted in Windows (or by the mic's own mute button): it delivers pure silence.</summary>
    public bool IsEndpointMuted { get; private set; }
    /// <summary>UI thread.</summary>
    public event Action<bool>? EndpointMutedChanged;
    MMDevice? _device;
    public string DeviceName { get; private set; } = "";

    public AudioCapture(System.Windows.Threading.Dispatcher ui)
    {
        _ui = ui;
        _watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchdog.Tick += (_, _) =>
        {
            if (!_wanted) return;
            var idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastDataTicks));
            if (_cap == null || idle > TimeSpan.FromSeconds(IsBlocked ? 5 : 2)) Restart();
        };
    }

    public void Start(string deviceId)
    {
        _deviceId = deviceId;
        _wanted = true;
        Restart();
        _watchdog.Start();
    }

    public void Restart()
    {
        StopDevice();
        if (!_wanted) return;
        Interlocked.Exchange(ref _lastDataTicks, DateTime.UtcNow.Ticks);
        _diagIn = _diagOut = 0; _diagInSq = _diagOutSq = 0; _diagUntil = DateTime.UtcNow.AddSeconds(5).Ticks;
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = AudioDevices.Find(en, DataFlow.Capture, _deviceId) ?? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            DeviceName = dev.FriendlyName;
            _device = dev;
            dev.AudioEndpointVolume.OnVolumeNotification += OnEndpointVolume;
            SetEndpointMuted(dev.AudioEndpointVolume.Mute);
            var cap = new WasapiCapture(dev, true, 30);
            _fmt = cap.WaveFormat;
            _rs = new WdlResampler();
            _rs.SetMode(true, 2, false);
            _rs.SetFilterParms();
            _rs.SetFeedMode(true);
            _rs.SetRates(_fmt.SampleRate, OutRate);
            _framePos = 0;
            cap.DataAvailable += OnData;
            cap.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Log.Write($"capture stopped: {e.Exception.Message}");
            };
            cap.StartRecording();
            _cap = cap;
            SetBlocked(false);
            Log.Write($"capture: {dev.FriendlyName} {_fmt.SampleRate} Hz x{_fmt.Channels} {_fmt.Encoding}/{_fmt.BitsPerSample}");
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            SetBlocked(true);
        }
        catch (Exception ex)
        {
            Log.Write($"capture: start failed: {ex.Message}");
        }
    }

    static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is COMException c && (uint)c.HResult == 0x80070005);

    void SetBlocked(bool blocked)
    {
        if (IsBlocked == blocked) return;
        IsBlocked = blocked;
        _ui.BeginInvoke(() => BlockedChanged?.Invoke(blocked));
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        Interlocked.Exchange(ref _lastDataTicks, DateTime.UtcNow.Ticks);
        var fmt = _fmt!;
        int ch = fmt.Channels;
        int bytesPerSample = fmt.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPerSample * ch);
        if (frames == 0) return;
        if (_mono.Length < frames) _mono = new float[frames];

        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt is WaveFormatExtensible x && x.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        var src = e.Buffer.AsSpan(0, e.BytesRecorded);
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++)
            {
                int o = (f * ch + c) * bytesPerSample;
                sum += isFloat ? BitConverter.ToSingle(src.Slice(o, 4))
                     : bytesPerSample switch
                     {
                         2 => BitConverter.ToInt16(src.Slice(o, 2)) / 32768f,
                         3 => ((src[o] << 8 | src[o + 1] << 16 | src[o + 2] << 24) >> 8) / 8388608f,
                         4 => BitConverter.ToInt32(src.Slice(o, 4)) / 2147483648f,
                         _ => 0,
                     };
            }
            _mono[f] = sum / ch;
        }

        if (RawSamples is { } raw) raw(_mono.AsSpan(0, frames).ToArray(), fmt.SampleRate);

        if (_diagUntil != 0)
        {
            for (int f = 0; f < frames; f++) _diagInSq += _mono[f] * _mono[f];
            _diagIn += frames;
        }

        var rs = _rs!;
        int inNeeded = rs.ResamplePrepare(frames, 1, out var inBuf, out int inOff);
        Array.Copy(_mono, 0, inBuf, inOff, Math.Min(frames, inNeeded));
        int maxOut = frames * OutRate / fmt.SampleRate + 32;
        if (_res.Length < maxOut) _res = new float[maxOut];
        int outCount = rs.ResampleOut(_res, 0, Math.Min(frames, inNeeded), maxOut, 1);

        if (_diagUntil != 0)
        {
            for (int i = 0; i < outCount; i++) _diagOutSq += _res[i] * _res[i];
            _diagOut += outCount;
            if (DateTime.UtcNow.Ticks > _diagUntil)
            {
                _diagUntil = 0;
                static string Db(double sq, long n) => n == 0 ? "-inf" : $"{10 * Math.Log10(Math.Max(sq / n, 1e-12)):0.0}";
                Log.Write($"mic check: in {_diagIn} samples @ {fmt.SampleRate} Hz, {Db(_diagInSq, _diagIn)} dBFS → out {_diagOut} @ 16000 Hz, {Db(_diagOutSq, _diagOut)} dBFS");
            }
        }

        for (int i = 0; i < outCount; i++)
        {
            float v = Math.Clamp(_res[i], -1f, 1f);
            _frame[_framePos++] = (short)(v * 32767);
            if (_framePos == FrameSamples)
            {
                double acc = 0;
                for (int k = 0; k < FrameSamples; k++) acc += (double)_frame[k] * _frame[k];
                float rms = (float)(Math.Sqrt(acc / FrameSamples) / 32768.0);
                var bytes = new byte[FrameSamples * 2];
                Buffer.BlockCopy(_frame, 0, bytes, 0, bytes.Length);
                _framePos = 0;
                Frame?.Invoke(bytes, rms);
            }
        }
    }

    void OnEndpointVolume(AudioVolumeNotificationData data) => SetEndpointMuted(data.Muted);

    void SetEndpointMuted(bool muted)
    {
        if (IsEndpointMuted == muted) return;
        IsEndpointMuted = muted;
        Log.Write($"capture: endpoint {(muted ? "MUTED in Windows" : "unmuted")}");
        _ui.BeginInvoke(() => EndpointMutedChanged?.Invoke(muted));
    }

    /// <summary>User asked to unmute the Windows endpoint.</summary>
    public void UnmuteEndpoint()
    {
        try { if (_device != null) _device.AudioEndpointVolume.Mute = false; }
        catch (Exception ex) { Log.Write($"capture: unmute failed: {ex.Message}"); }
    }

    void StopDevice()
    {
        if (_device != null)
        {
            try { _device.AudioEndpointVolume.OnVolumeNotification -= OnEndpointVolume; } catch { }
            _device = null;
        }
        var cap = _cap;
        _cap = null;
        if (cap == null) return;
        cap.DataAvailable -= OnData;
        try { cap.StopRecording(); } catch { }
        try { cap.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _wanted = false;
        _watchdog.Stop();
        StopDevice();
    }
}
