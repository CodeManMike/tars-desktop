namespace TarsClient.Tests;

[TestFixture]
public sealed class WakeWordsTests
{
    #region Tests

    [TestCase("TARS, what's the time?", true)]
    [TestCase("hey tars", true)]
    [TestCase("the stars are out", false)]
    [TestCase("", false)]
    public void Mentions_MatchesWholeWordsOnly(string text, bool expected)
    {
        #region Arrange
        string[] wakeWords = ["tars"];
        #endregion

        #region Act
        var mentioned = WakeWords.Mentions(text, wakeWords);
        #endregion

        #region Assert
        Assert.That(mentioned, Is.EqualTo(expected));
        #endregion
    }

    [Test]
    public void Parse_LowercasesTrimsAndAddsTheAssistantName()
    {
        #region Arrange
        const string wakeWords = " Hey TARS , computer,,";
        #endregion

        #region Act
        var parsed = WakeWords.Parse(wakeWords, "Tars");
        #endregion

        #region Assert
        Assert.That(parsed, Is.EqualTo(new[] { "hey tars", "computer", "tars" }));
        #endregion
    }

    [Test]
    public void Parse_NothingConfigured_FallsBackToTars()
    {
        #region Arrange
        string? wakeWords = null;
        #endregion

        #region Act
        var parsed = WakeWords.Parse(wakeWords, null);
        #endregion

        #region Assert
        Assert.That(parsed, Is.EqualTo(new[] { "tars" }));
        #endregion
    }

    #endregion
}
