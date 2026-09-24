namespace TarsClient.Tests;

[TestFixture]
public sealed class AudioPlaybackTests
{
    #region Tests

    [TestCase(0.5f, 0.5f)]
    [TestCase(-0.8f, -0.8f)]
    public void SoftClip_BelowTheKnee_IsLinear(float input, float expected)
    {
        #region Arrange
        // The sample comes from the test case.
        #endregion

        #region Act
        var output = AudioPlayback.SoftClip(input);
        #endregion

        #region Assert
        Assert.That(output, Is.EqualTo(expected));
        #endregion
    }

    [TestCase(1.5f)]
    [TestCase(-3f)]
    [TestCase(100f)]
    public void SoftClip_AboveTheKnee_NeverExceedsFullScale(float input)
    {
        #region Arrange
        // The sample comes from the test case.
        #endregion

        #region Act
        var output = AudioPlayback.SoftClip(input);
        #endregion

        #region Assert
        Assert.That(Math.Abs(output), Is.InRange(0.8f, 1.0f));
        Assert.That(Math.Sign(output), Is.EqualTo(Math.Sign(input)));
        #endregion
    }

    [Test]
    public void ChimeSamples_ThreeBlipsAtHalfScale()
    {
        #region Arrange
        int expectedLength = (int)(AudioPlayback.Rate * 0.22) * 2 + (int)(AudioPlayback.Rate * 0.18);
        #endregion

        #region Act
        var chime = AudioPlayback.ChimeSamples();
        #endregion

        #region Assert
        Assert.That(chime, Has.Length.EqualTo(expectedLength));
        Assert.That(chime.Max(Math.Abs), Is.LessThanOrEqualTo(0.5f));
        #endregion
    }

    #endregion
}
