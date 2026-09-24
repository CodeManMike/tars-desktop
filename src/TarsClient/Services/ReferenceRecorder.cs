namespace TarsClient.Services;

/// <summary>
/// Records a clip from the mic at its native rate (for a reference voice or a voiceprint): collects N seconds,
/// trims leading and trailing silence, normalises the peak to -3 dBFS and writes a 16-bit mono WAV.
/// </summary>
public sealed class ReferenceRecorder
{
    #region Fields

    private const double SilenceDbfs = -45;
    private const double PeakDbfs = -3;
    private const int PadMs = 150;

    private readonly AudioCapture _capture;
    private readonly List<float> _samples = [];
    private TaskCompletionSource? _done;
    private int _rate;
    private int _target;

    #endregion

    #region Constructor

    /// <summary>Records from <paramref name="capture"/>'s raw (pre-resampling) stream.</summary>
    public ReferenceRecorder(AudioCapture capture) => _capture = capture;

    #endregion

    #region Properties

    /// <summary>Peak level of the last block (0–1), for a live meter.</summary>
    public float Level { get; private set; }

    #endregion

    #region Public Methods

    /// <summary>Records <paramref name="length"/> of audio into <paramref name="folder"/> and returns the WAV path.</summary>
    /// <exception cref="InvalidOperationException">No audio arrived, or it was mostly silence.</exception>
    public async Task<string> RecordAsync(TimeSpan length, string folder, CancellationToken ct)
    {
        lock (_samples) _samples.Clear();
        _rate = 0;
        _target = int.MaxValue;
        _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture.RawSamples += OnRaw;
        try
        {
            var start = DateTime.UtcNow;
            while (_rate == 0 && DateTime.UtcNow - start < TimeSpan.FromSeconds(3)) await Task.Delay(50, ct);
            if (_rate == 0) throw new InvalidOperationException("no audio from the microphone (muted or unplugged?)");
            _target = (int)(_rate * length.TotalSeconds);
            await _done.Task.WaitAsync(ct);
        }
        finally { _capture.RawSamples -= OnRaw; }

        float[] audio;
        lock (_samples) audio = [.. _samples];
        audio = TrimAndNormalise(audio, _rate);
        if (audio.Length < _rate * 5) throw new InvalidOperationException("mostly silence: speak up or move closer to the mic");

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"my-voice-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        WriteWav(path, audio, _rate);
        return path;
    }

    #endregion

    #region Private Methods

    private void OnRaw(float[] mono, int rate)
    {
        _rate = rate;
        float peak = 0;
        foreach (var v in mono) peak = Math.Max(peak, Math.Abs(v));
        Level = peak;
        lock (_samples)
        {
            _samples.AddRange(mono);
            if (_samples.Count >= _target) _done?.TrySetResult();
        }
    }

    /// <summary>Trims silence at both ends in 20 ms windows, keeps a little air around the speech and normalises the peak.</summary>
    private static float[] TrimAndNormalise(float[] a, int rate)
    {
        int window = rate / 50;
        double threshold = Math.Pow(10, SilenceDbfs / 20);
        bool Loud(int w)
        {
            double sum = 0;
            int end = Math.Min(a.Length, (w + 1) * window);
            for (int i = w * window; i < end; i++) sum += a[i] * a[i];
            return Math.Sqrt(sum / Math.Max(1, end - w * window)) > threshold;
        }

        int windows = a.Length / window, first = 0, last = windows - 1;
        while (first < windows && !Loud(first)) first++;
        if (first >= windows) return [];
        while (last > first && !Loud(last)) last--;

        int pad = rate * PadMs / 1000;
        var clip = a[Math.Max(0, first * window - pad)..Math.Min(a.Length, (last + 1) * window + pad)];
        float peak = clip.Max(Math.Abs);
        if (peak <= 0) return clip;

        float gain = (float)(Math.Pow(10, PeakDbfs / 20) / peak);
        for (int i = 0; i < clip.Length; i++) clip[i] *= gain;
        return clip;
    }

    private static void WriteWav(string path, float[] a, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = a.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + bytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(bytes);
        foreach (var v in a) w.Write((short)Math.Clamp(v * 32767f, -32768f, 32767f));
    }

    #endregion
}
