using System.IO;

namespace TarsClient.Services;

/// <summary>
/// Records a voice reference clip from the mic at its native rate: collects N seconds, trims leading/trailing
/// silence, normalises the peak to -3 dBFS and writes a 16-bit mono WAV.
/// </summary>
public sealed class ReferenceRecorder
{
    readonly AudioCapture _capture;
    readonly List<float> _samples = new();
    int _rate;
    TaskCompletionSource? _done;
    int _target;

    public ReferenceRecorder(AudioCapture capture) => _capture = capture;

    /// <summary>Peak level of the last block (0..1), for a live meter.</summary>
    public float Level { get; private set; }

    public async Task<string> RecordAsync(TimeSpan length, string folder, CancellationToken ct)
    {
        lock (_samples) _samples.Clear();
        _rate = 0;
        _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _target = int.MaxValue;
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
        lock (_samples) audio = _samples.ToArray();
        audio = TrimAndNormalise(audio, _rate);
        if (audio.Length < _rate * 5) throw new InvalidOperationException("mostly silence: speak up or move closer to the mic");

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"my-voice-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        WriteWav(path, audio, _rate);
        return path;
    }

    void OnRaw(float[] mono, int rate)
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

    static float[] TrimAndNormalise(float[] a, int rate)
    {
        // 20 ms windows; anything below -45 dBFS RMS at either end is silence. Keep 150 ms of air around the speech.
        int win = rate / 50;
        double threshold = Math.Pow(10, -45 / 20.0);
        bool Loud(int w)
        {
            double sum = 0;
            int end = Math.Min(a.Length, (w + 1) * win);
            for (int i = w * win; i < end; i++) sum += a[i] * a[i];
            return Math.Sqrt(sum / Math.Max(1, end - w * win)) > threshold;
        }
        int windows = a.Length / win, first = 0, last = windows - 1;
        while (first < windows && !Loud(first)) first++;
        while (last > first && !Loud(last)) last--;
        if (first >= windows) return [];
        int pad = rate * 150 / 1000;
        int s = Math.Max(0, first * win - pad), e = Math.Min(a.Length, (last + 1) * win + pad);
        var outp = a[s..e];
        float peak = outp.Max(Math.Abs);
        if (peak > 0)
        {
            float gain = (float)(Math.Pow(10, -3 / 20.0) / peak);
            for (int i = 0; i < outp.Length; i++) outp[i] *= gain;
        }
        return outp;
    }

    static void WriteWav(string path, float[] a, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = a.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var v in a) w.Write((short)Math.Clamp(v * 32767f, -32768f, 32767f));
    }
}
