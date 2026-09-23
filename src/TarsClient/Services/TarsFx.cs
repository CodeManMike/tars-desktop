namespace TarsClient.Services;

/// <summary>
/// "TARS FX": makes any voice sound more like it comes out of a machine, without imitating anyone.
///  1. Varispeed down ~5 %: a touch deeper and more measured.
///  2. Band-limit 110 Hz – 6.5 kHz: a speaker in a metal chassis.
///  3. A short comb (3.1 ms, low gain): a faint metallic ring.
/// Stateful, so a streamed sentence can be processed chunk by chunk. 24 kHz mono in and out.
/// </summary>
public sealed class TarsFx
{
    const int Rate = AudioPlayback.Rate;
    readonly double Ratio;              // input samples consumed per output sample: < 1 = lower and slower
    readonly float _ring;

    public TarsFx(double pitch = 0.95, double ring = 0.22)
    {
        Ratio = Math.Clamp(pitch, 0.8, 1.0);
        _ring = (float)Math.Clamp(ring, 0, 0.6);
    }

    double _pos;                        // fractional read position into (prev + chunk)
    float _prev;
    readonly Biquad _hp = Biquad.HighPass(Rate, 110, 0.707);
    readonly Biquad _lp = Biquad.LowPass(Rate, 6500, 0.707);
    readonly float[] _comb = new float[(int)(Rate * 0.0031)];
    int _combPos;

    public float[] Process(ReadOnlySpan<float> input)
    {
        if (input.Length == 0) return [];
        var output = new List<float>((int)(input.Length / Ratio) + 2);
        // Linear-interpolation varispeed across chunk boundaries: index -1 is the previous chunk's last sample.
        while (_pos < input.Length - 1)
        {
            int i = (int)Math.Floor(_pos);
            float frac = (float)(_pos - i);
            float a = i < 0 ? _prev : input[i];
            float b = input[i + 1];
            output.Add(a + (b - a) * frac);
            _pos += Ratio;
        }
        _pos -= input.Length;
        _prev = input[^1];

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(output);
        for (int k = 0; k < span.Length; k++)
        {
            float x = _lp.Next(_hp.Next(span[k]));
            float delayed = _comb[_combPos];
            float y = x + _ring * delayed;
            _comb[_combPos] = x;
            _combPos = (_combPos + 1) % _comb.Length;
            span[k] = y * 0.9f;
        }
        return output.ToArray();
    }

    sealed class Biquad
    {
        readonly float b0, b1, b2, a1, a2;
        float z1, z2;

        Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            this.b0 = (float)(b0 / a0); this.b1 = (float)(b1 / a0); this.b2 = (float)(b2 / a0);
            this.a1 = (float)(a1 / a0); this.a2 = (float)(a2 / a0);
        }

        public float Next(float x)
        {
            float y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }

        public static Biquad LowPass(double fs, double f, double q)
        {
            double w = 2 * Math.PI * f / fs, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
            return new((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public static Biquad HighPass(double fs, double f, double q)
        {
            double w = 2 * Math.PI * f / fs, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
            return new((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }
    }
}
