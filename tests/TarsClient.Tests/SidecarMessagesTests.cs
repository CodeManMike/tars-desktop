namespace TarsClient.Tests;

/// <summary>Golden JSON for the control messages to the speech sidecar's <c>/stt/stream</c>.</summary>
[TestFixture]
public sealed class SidecarMessagesTests
{
    #region Tests

    [TestCaseSource(nameof(Messages))]
    public void Message_SerializesToGoldenJson(object message, string expected)
    {
        #region Arrange
        // The message and its golden JSON come from the test case.
        #endregion

        #region Act
        var json = JsonSerializer.Serialize(message);
        #endregion

        #region Assert
        Assert.That(json, Is.EqualTo(expected));
        #endregion
    }

    #endregion

    #region Test Data

    private static IEnumerable<TestCaseData> Messages()
    {
        yield return Case(
            "Config_WithVoiceLock",
            SidecarMessages.Config("large-v3-turbo", "cuda", @"C:\voices\voiceprint.npy", 0.67),
            """{"type":"config","model":"large-v3-turbo","device":"cuda","hotwords":"TARS","speaker":"C:\\voices\\voiceprint.npy","speaker_threshold":0.67}""");
        yield return Case(
            "Config_GameMode",
            SidecarMessages.Config("small.en", "cpu", "", 0.67),
            """{"type":"config","model":"small.en","device":"cpu","hotwords":"TARS","speaker":"","speaker_threshold":0.67}""");
        yield return Case("Ptt_Start", SidecarMessages.Ptt(start: true), """{"type":"ptt","state":"start"}""");
        yield return Case("Ptt_End", SidecarMessages.Ptt(start: false), """{"type":"ptt","state":"end"}""");
        yield return Case("Reset", SidecarMessages.Reset(), """{"type":"reset"}""");
    }

    private static TestCaseData Case(string name, object message, string json) => new TestCaseData(message, json).SetName(name);

    #endregion
}
