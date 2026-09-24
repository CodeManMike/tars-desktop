using System.Runtime.InteropServices;

namespace TarsClient.Services;

/// <summary>
/// "TARS FX" makes any voice sound more like it comes out of a machine, without imitating anyone:
/// <list type="number">
/// <item>varispeed down about 5 %, a touch deeper and more measured;</item>
/// <item>a band-limit of 110 Hz – 6.5 kHz, a speaker in a metal chassis;</item>
/// <item>a short comb (3.1 ms, low gain), a faint metallic ring.</item>
/// </list>
/// It's stateful, so we can process a streamed sentence chunk by chunk. 24 kHz mono in and out.
/// </summary>
public sealed class TarsFx
{
    #region Fields

    private const int Rate = AudioPlayback.Rate;
    private const float OutputGain = 0.9f;

    private readonly double _ratio;          // input samples consumed per output sample: < 1 is lower and slower
    private readonly float _ring;
    private readonly Biquad _highPass = Biquad.HighPass(Rate, 110, 0.707);
    private readonly Biquad _lowPass = Biquad.LowPass(Rate, 6500, 0.707);
    private readonly float[] _comb = new float[(int)(Rate * 0.0031)];
    private int _combPos;
    private double _pos;                     // fractional read position into (previous sample + chunk)
    private float _prev;

    #endregion

    #region Constructor

    /// <summary>Creates the effect.</summary>
    /// <param name="pitch">Varispeed ratio, clamped to 0.8–1.0 (1.0 is unchanged).</param>
    /// <param name="ring">Comb gain, clamped to 0–0.6.</param>
    public TarsFx(double pitch = 0.95, double ring = 0.22)
    {
        _ratio = Math.Clamp(pitch, 0.8, 1.0);
        _ring = (float)Math.Clamp(ring, 0, 0.6);
    }

    #endregion

    #region Public Methods

    /// <summary>Processes one chunk; call it again with the next chunk of the same sentence.</summary>
    public float[] Process(ReadOnlySpan<float> input)
    {
        if (input.Length == 0) return [];

        var output = Varispeed(input);
        var span = CollectionsMarshal.AsSpan(output);
        for (int k = 0; k < span.Length; k++)
        {
            float x = _lowPass.Next(_highPass.Next(span[k]));
            float delayed = _comb[_combPos];
            _comb[_combPos] = x;
            _combPos = (_combPos + 1) % _comb.Length;
            span[k] = (x + _ring * delayed) * OutputGain;
        }
        return output.ToArray();
    }

    #endregion

    #region Private Methods

    /// <summary>Linear-interpolation resampling across chunk boundaries (index -1 is the previous chunk's last sample).</summary>
    private List<float> Varispeed(ReadOnlySpan<float> input)
    {
        var output = new List<float>((int)(input.Length / _ratio) + 2);
        while (_pos < input.Length - 1)
        {
            int i = (int)Math.Floor(_pos);
            float frac = (float)(_pos - i);
            float a = i < 0 ? _prev : input[i];
            float b = input[i + 1];
            output.Add(a + (b - a) * frac);
            _pos += _ratio;
        }
        _pos -= input.Length;
        _prev = input[^1];
        return output;
    }

    #endregion

    #region Nested Types

    /// <summary>A direct-form-II transposed biquad (RBJ cookbook coefficients).</summary>
    private sealed class Biquad
    {
        private readonly float _b0, _b1, _b2, _a1, _a2;
        private float _z1, _z2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = (float)(b0 / a0);
            _b1 = (float)(b1 / a0);
            _b2 = (float)(b2 / a0);
            _a1 = (float)(a1 / a0);
            _a2 = (float)(a2 / a0);
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

        public float Next(float x)
        {
            float y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }

    #endregion
}
