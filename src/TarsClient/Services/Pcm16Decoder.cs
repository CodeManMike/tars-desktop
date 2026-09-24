namespace TarsClient.Services;

/// <summary>
/// Decodes a streamed Int16 LE PCM body into floats. A network read can split a sample, so we carry the odd byte
/// over to the next chunk.
/// </summary>
public sealed class Pcm16Decoder
{
    #region Fields

    private int _carry = -1;

    #endregion

    #region Public Methods

    /// <summary>Decodes <paramref name="bytes"/> (plus any byte carried from the previous chunk).</summary>
    public float[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return [];

        var samples = new float[(bytes.Length + (_carry >= 0 ? 1 : 0)) / 2];
        int si = 0, bi = 0;
        if (_carry >= 0)
        {
            samples[si++] = (short)(_carry | bytes[0] << 8) / 32768f;
            bi = 1;
            _carry = -1;
        }
        for (; bi + 1 < bytes.Length; bi += 2) samples[si++] = (short)(bytes[bi] | bytes[bi + 1] << 8) / 32768f;
        if (bi < bytes.Length) _carry = bytes[bi];
        return si == samples.Length ? samples : samples[..si];
    }

    #endregion
}
