namespace TarsClient.Tests;

/// <summary>
/// Golden JSON for every client → server message. The protocol is frozen and agreed with the server, so a change here
/// is a wire change and needs the server's sign-off first.
/// </summary>
[TestFixture]
public sealed class ClientMessagesTests
{
    #region Fields

    private static readonly Utterance Sample = new("what time is it", "vad", 1200, 1800, 250, -0.25, 0.01, "large-v3-turbo");

    #endregion

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

    [Test]
    public void Utterance_StartedMsAgo_IsClipLengthPlusTranscriptionTime()
    {
        #region Arrange
        var utterance = Sample with { DurationMs = 4000, SttMs = 900 };
        #endregion

        #region Act
        var json = JsonSerializer.SerializeToElement(ClientMessages.Utterance(utterance, "wake"));
        #endregion

        #region Assert
        Assert.That(json.GetProperty("started_ms_ago").GetInt32(), Is.EqualTo(4900));
        #endregion
    }

    #endregion

    #region Test Data

    private static IEnumerable<TestCaseData> Messages()
    {
        yield return Case("Client_LocalStt", ClientMessages.Client(localStt: true), """{"type":"client","name":"tars-desktop","tts":"local","stt":"local"}""");
        yield return Case("Client_ServerStt", ClientMessages.Client(localStt: false), """{"type":"client","name":"tars-desktop","tts":"local","stt":"server"}""");
        yield return Case("Mode", ClientMessages.Mode("wake"), """{"type":"mode","mode":"wake"}""");
        yield return Case("Ptt_Start", ClientMessages.Ptt(start: true), """{"type":"ptt","state":"start"}""");
        yield return Case("Ptt_End", ClientMessages.Ptt(start: false), """{"type":"ptt","state":"end"}""");
        yield return Case("Text", ClientMessages.Text("hello", speak: true), """{"type":"text","text":"hello","speak":true}""");
        yield return Case("Playback_Start", ClientMessages.Playback(start: true), """{"type":"playback","state":"start"}""");
        yield return Case("Playback_End", ClientMessages.Playback(start: false), """{"type":"playback","state":"end"}""");
        yield return Case("Stop", ClientMessages.Stop(), """{"type":"stop"}""");
        yield return Case("TestVoice", ClientMessages.TestVoice(), """{"type":"test_voice"}""");
        yield return Case("Personality", ClientMessages.Personality(75, 95, 85), """{"type":"settings","humor":75,"honesty":95,"brevity":85}""");
        yield return Case("CancelTimer", ClientMessages.CancelTimer("t1"), """{"type":"cancel_timer","id":"t1"}""");
        yield return Case("Dismiss", ClientMessages.Dismiss(), """{"type":"dismiss"}""");
        yield return Case(
            "Utterance",
            ClientMessages.Utterance(Sample, "wake"),
            """{"type":"utterance","text":"what time is it","source":"wake","speech_ms":1200,"stt_ms":250,"logprob":-0.25,"no_speech_prob":0.01,"model":"large-v3-turbo","duration_ms":1800,"started_ms_ago":2050}""");
    }

    private static TestCaseData Case(string name, object message, string json) => new TestCaseData(message, json).SetName(name);

    #endregion
}
