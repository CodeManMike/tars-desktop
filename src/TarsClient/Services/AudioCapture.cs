using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace TarsClient.Services;

/// <summary>
/// WASAPI capture of the chosen (or default) microphone, converted to 16 kHz mono Int16 LE and emitted in 512-sample
/// (1,024-byte) frames with an RMS level. We recover from unplug and replug: device events and a 2 s no-data
/// watchdog both trigger a restart. A mic muted in Windows is detected and reported, since it delivers pure silence.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    #region Fields

    /// <summary>Output sample rate.</summary>
    public const int OutRate = 16000;

    /// <summary>Samples per emitted frame (32 ms).</summary>
    public const int FrameSamples = 512;

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _watchdog;
    private readonly short[] _frame = new short[FrameSamples];
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private WdlResampler? _resampler;
    private WaveFormat? _format;
    private string _deviceId = "";
    private float[] _mono = [];
    private float[] _resampled = [];
    private int _framePos;
    private long _lastDataTicks;
    private bool _wanted;

    #endregion

    #region Constructor

    /// <summary>Creates the capture; events that touch UI state are raised on <paramref name="ui"/>.</summary>
    public AudioCapture(Dispatcher ui)
    {
        _ui = ui;
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchdog.Tick += (_, _) => Watchdog();
    }

    #endregion

    #region Properties

    /// <summary>Windows privacy settings are blocking the mic.</summary>
    public bool IsBlocked { get; private set; }

    /// <summary>The endpoint is muted in Windows (or by the mic's own mute button).</summary>
    public bool IsEndpointMuted { get; private set; }

    /// <summary>The friendly name of the device we're capturing from.</summary>
    public string DeviceName { get; private set; } = "";

    #endregion

    #region Events

    /// <summary>A 16 kHz frame and its RMS (0–1). Raised on the capture thread: keep handlers cheap.</summary>
    public event Action<byte[], float>? Frame;

    /// <summary>Full-rate mono samples before resampling (for recording clips). Raised on the capture thread.</summary>
    public event Action<float[], int>? RawSamples;

    /// <summary>Privacy blocking changed. UI thread.</summary>
    public event Action<bool>? BlockedChanged;

    /// <summary>Windows mute changed. UI thread.</summary>
    public event Action<bool>? EndpointMutedChanged;

    #endregion

    #region Public Methods

    /// <summary>Starts capturing from <paramref name="deviceId"/> (empty means the Windows default).</summary>
    public void Start(string deviceId)
    {
        _deviceId = deviceId;
        _wanted = true;
        Restart();
        _watchdog.Start();
    }

    /// <summary>Re-opens the device, e.g. after a headset replug.</summary>
    public void Restart()
    {
        StopDevice();
        if (!_wanted) return;
        Interlocked.Exchange(ref _lastDataTicks, DateTime.UtcNow.Ticks);
        try
        {
            OpenDevice();
            SetBlocked(false);
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            SetBlocked(true);
        }
        catch (COMException ex)
        {
            Log.Write($"capture: start failed: {ex.Message}");
        }
    }

    /// <summary>Unmutes the endpoint in Windows (only ever on the user's request).</summary>
    public void UnmuteEndpoint()
    {
        try
        {
            if (_device != null) _device.AudioEndpointVolume.Mute = false;
        }
        catch (COMException ex) { Log.Write($"capture: unmute failed: {ex.Message}"); }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _wanted = false;
        _watchdog.Stop();
        StopDevice();
    }

    #endregion

    #region Private Methods

    private void OpenDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = AudioDevices.Find(enumerator, DataFlow.Capture, _deviceId)
                     ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        DeviceName = device.FriendlyName;
        _device = device;
        device.AudioEndpointVolume.OnVolumeNotification += OnEndpointVolume;
        SetEndpointMuted(device.AudioEndpointVolume.Mute);

        var capture = new WasapiCapture(device, true, 30);
        _format = capture.WaveFormat;
        _resampler = new WdlResampler();
        _resampler.SetMode(true, 2, false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(true);
        _resampler.SetRates(_format.SampleRate, OutRate);
        _framePos = 0;

        capture.DataAvailable += OnData;
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) Log.Write($"capture stopped: {e.Exception.Message}");
        };
        capture.StartRecording();
        _capture = capture;
        Log.Write($"capture: {device.FriendlyName} {_format.SampleRate} Hz x{_format.Channels} {_format.Encoding}/{_format.BitsPerSample}");
    }

    private void StopDevice()
    {
        if (_device != null)
        {
            try { _device.AudioEndpointVolume.OnVolumeNotification -= OnEndpointVolume; }
            catch (COMException) { }
            _device = null;
        }

        var capture = _capture;
        _capture = null;
        if (capture == null) return;
        capture.DataAvailable -= OnData;
        try
        {
            capture.StopRecording();
            capture.Dispose();
        }
        catch (COMException) { }
    }

    /// <summary>No data for 2 s (5 s while blocked) means the device went away: we reopen it.</summary>
    private void Watchdog()
    {
        if (!_wanted) return;
        var idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastDataTicks));
        if (_capture == null || idle > TimeSpan.FromSeconds(IsBlocked ? 5 : 2)) Restart();
    }

    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is COMException com && (uint)com.HResult == 0x80070005);

    private void SetBlocked(bool blocked)
    {
        if (IsBlocked == blocked) return;
        IsBlocked = blocked;
        _ui.BeginInvoke(() => BlockedChanged?.Invoke(blocked));
    }

    private void OnEndpointVolume(AudioVolumeNotificationData data) => SetEndpointMuted(data.Muted);

    private void SetEndpointMuted(bool muted)
    {
        if (IsEndpointMuted == muted) return;
        IsEndpointMuted = muted;
        Log.Write($"capture: endpoint {(muted ? "MUTED in Windows" : "unmuted")}");
        _ui.BeginInvoke(() => EndpointMutedChanged?.Invoke(muted));
    }

    /// <summary>Device format → mono float → 16 kHz → Int16 frames.</summary>
    private void OnData(object? sender, WaveInEventArgs e)
    {
        Interlocked.Exchange(ref _lastDataTicks, DateTime.UtcNow.Ticks);
        var format = _format!;
        int frames = DownmixToMono(e.Buffer.AsSpan(0, e.BytesRecorded), format);
        if (frames == 0) return;

        RawSamples?.Invoke(_mono.AsSpan(0, frames).ToArray(), format.SampleRate);

        var resampler = _resampler!;
        int accepted = Math.Min(frames, resampler.ResamplePrepare(frames, 1, out var inBuf, out int inOff));
        Array.Copy(_mono, 0, inBuf, inOff, accepted);
        int maxOut = frames * OutRate / format.SampleRate + 32;
        if (_resampled.Length < maxOut) _resampled = new float[maxOut];
        int outCount = resampler.ResampleOut(_resampled, 0, accepted, maxOut, 1);

        for (int i = 0; i < outCount; i++)
        {
            _frame[_framePos++] = (short)(Math.Clamp(_resampled[i], -1f, 1f) * 32767);
            if (_framePos == FrameSamples) EmitFrame();
        }
    }

    private int DownmixToMono(ReadOnlySpan<byte> src, WaveFormat format)
    {
        int channels = format.Channels;
        int bytesPerSample = format.BitsPerSample / 8;
        int frames = src.Length / (bytesPerSample * channels);
        if (_mono.Length < frames) _mono = new float[frames];

        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int o = (f * channels + c) * bytesPerSample;
                sum += isFloat ? BitConverter.ToSingle(src.Slice(o, 4)) : ReadPcm(src, o, bytesPerSample);
            }
            _mono[f] = sum / channels;
        }
        return frames;
    }

    private static float ReadPcm(ReadOnlySpan<byte> src, int o, int bytesPerSample) => bytesPerSample switch
    {
        2 => BitConverter.ToInt16(src.Slice(o, 2)) / 32768f,
        3 => ((src[o] << 8 | src[o + 1] << 16 | src[o + 2] << 24) >> 8) / 8388608f,
        4 => BitConverter.ToInt32(src.Slice(o, 4)) / 2147483648f,
        _ => 0,
    };

    private void EmitFrame()
    {
        double acc = 0;
        for (int k = 0; k < FrameSamples; k++) acc += (double)_frame[k] * _frame[k];
        float rms = (float)(Math.Sqrt(acc / FrameSamples) / 32768.0);
        var bytes = new byte[FrameSamples * 2];
        Buffer.BlockCopy(_frame, 0, bytes, 0, bytes.Length);
        _framePos = 0;
        Frame?.Invoke(bytes, rms);
    }

    #endregion
}
