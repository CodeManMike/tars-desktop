namespace TarsClient.Tests;

[TestFixture]
public sealed class Pcm16DecoderTests
{
    #region Tests

    [Test]
    public void Decode_WholeSamples_ScalesToUnitRange()
    {
        #region Arrange
        var decoder = new Pcm16Decoder();
        byte[] bytes = [0x00, 0x40, 0x00, 0xC0];   // +16384, -16384
        #endregion

        #region Act
        var samples = decoder.Decode(bytes);
        #endregion

        #region Assert
        Assert.That(samples, Is.EqualTo(new[] { 0.5f, -0.5f }));
        #endregion
    }

    [Test]
    public void Decode_SampleSplitAcrossChunks_CarriesTheOddByte()
    {
        #region Arrange
        var decoder = new Pcm16Decoder();
        byte[] first = [0x00, 0x40, 0x00];
        byte[] second = [0xC0, 0xFF, 0x7F];
        #endregion

        #region Act
        var a = decoder.Decode(first);
        var b = decoder.Decode(second);
        #endregion

        #region Assert
        Assert.That(a, Is.EqualTo(new[] { 0.5f }));
        Assert.That(b, Is.EqualTo(new[] { -0.5f, 32767 / 32768f }));
        #endregion
    }

    [Test]
    public void Decode_Empty_ReturnsNothing()
    {
        #region Arrange
        var decoder = new Pcm16Decoder();
        #endregion

        #region Act
        var samples = decoder.Decode([]);
        #endregion

        #region Assert
        Assert.That(samples, Is.Empty);
        #endregion
    }

    #endregion
}
