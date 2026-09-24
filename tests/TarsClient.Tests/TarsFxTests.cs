namespace TarsClient.Tests;

[TestFixture]
public sealed class TarsFxTests
{
    #region Tests

    [Test]
    public void Process_LowerPitch_LengthensTheAudioByTheRatio()
    {
        #region Arrange
        var fx = new TarsFx(pitch: 0.9, ring: 0.2);
        var input = Tone(24000);
        #endregion

        #region Act
        var output = fx.Process(input);
        #endregion

        #region Assert
        Assert.That(output.Length, Is.EqualTo(24000 / 0.9).Within(2));
        #endregion
    }

    [Test]
    public void Process_ChunkedInput_MatchesTheLengthOfOneChunk()
    {
        #region Arrange
        var chunked = new TarsFx();
        var whole = new TarsFx();
        var input = Tone(9600);
        #endregion

        #region Act
        int chunkedLength = chunked.Process(input.AsSpan(0, 4801)).Length + chunked.Process(input.AsSpan(4801)).Length;
        int wholeLength = whole.Process(input).Length;
        #endregion

        #region Assert
        Assert.That(chunkedLength, Is.EqualTo(wholeLength).Within(1));
        #endregion
    }

    [Test]
    public void Process_OutputIsFinite()
    {
        #region Arrange
        var fx = new TarsFx(pitch: 0.8, ring: 0.6);
        #endregion

        #region Act
        var output = fx.Process(Tone(12000));
        #endregion

        #region Assert
        Assert.That(output.All(float.IsFinite), Is.True);
        #endregion
    }

    #endregion

    #region Private Methods

    private static float[] Tone(int length) =>
        Enumerable.Range(0, length).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 220 * i / 24000))).ToArray();

    #endregion
}
