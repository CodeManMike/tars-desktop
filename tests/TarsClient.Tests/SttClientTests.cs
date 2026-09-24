namespace TarsClient.Tests;

[TestFixture]
public sealed class SttClientTests
{
    #region Tests

    [Test]
    public void ParseUtterance_ReadsEveryField()
    {
        #region Arrange
        var message = JsonDocument.Parse(
            """{"type":"utterance","text":"TARS, lights off","source":"ptt","speech_ms":900,"duration_ms":1400,"stt_ms":180,"logprob":-0.31,"no_speech_prob":0.02,"model":"large-v3-turbo"}""").RootElement;
        #endregion

        #region Act
        var utterance = SttClient.ParseUtterance(message);
        #endregion

        #region Assert
        Assert.That(utterance, Is.EqualTo(new Utterance("TARS, lights off", "ptt", 900, 1400, 180, -0.31, 0.02, "large-v3-turbo")));
        #endregion
    }

    [Test]
    public void ParseUtterance_MissingFields_ReadAsEmptyAndZero()
    {
        #region Arrange
        var message = JsonDocument.Parse("""{"type":"utterance","text":"hi"}""").RootElement;
        #endregion

        #region Act
        var utterance = SttClient.ParseUtterance(message);
        #endregion

        #region Assert
        Assert.That(utterance, Is.EqualTo(new Utterance("hi", "", 0, 0, 0, 0, 0, "")));
        #endregion
    }

    #endregion
}
