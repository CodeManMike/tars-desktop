namespace TarsClient.Services;

/// <summary>
/// Every JSON message we send to the TARS server (spec section 5, client → server). Building them in one place
/// keeps the wire format in one file, and the golden-JSON tests pin each one.
/// </summary>
public static class ClientMessages
{
    #region Session

    /// <summary>Capabilities: we always voice replies locally; STT is local when the sidecar is up.</summary>
    public static object Client(bool localStt) =>
        new { type = "client", name = "tars-desktop", tts = "local", stt = localStt ? "local" : "server" };

    /// <summary>Listening mode: <c>ptt</c>, <c>wake</c> or <c>open</c>.</summary>
    public static object Mode(string mode) => new { type = "mode", mode };

    #endregion

    #region Speech

    /// <summary>Push-to-talk bracket for server-side STT (not sent while STT is local).</summary>
    public static object Ptt(bool start) => new { type = "ptt", state = start ? "start" : "end" };

    /// <summary>A transcript from the local STT. <c>started_ms_ago</c> lets the server judge the follow-up window by when we started speaking.</summary>
    public static object Utterance(Utterance u, string source) => new
    {
        type = "utterance",
        text = u.Text,
        source,
        speech_ms = u.SpeechMs,
        stt_ms = u.SttMs,
        logprob = u.LogProb,
        no_speech_prob = u.NoSpeechProb,
        model = u.Model,
        duration_ms = u.DurationMs,
        started_ms_ago = u.DurationMs + u.SttMs,
    };

    /// <summary>A typed message (or, when the server can't take <c>utterance</c>, an accepted local transcript).</summary>
    public static object Text(string text, bool speak) => new { type = "text", text, speak };

    #endregion

    #region Playback

    /// <summary>Our audio started or finished; the server keys its follow-up window off <c>end</c>.</summary>
    public static object Playback(bool start) => new { type = "playback", state = start ? "start" : "end" };

    /// <summary>Cancel this session's running turn.</summary>
    public static object Stop() => new { type = "stop" };

    /// <summary>Ask TARS to say its test line.</summary>
    public static object TestVoice() => new { type = "test_voice" };

    #endregion

    #region Settings, timers and alarms

    /// <summary>The personality sliders (the server broadcasts the result to every client).</summary>
    public static object Personality(double humor, double honesty, double brevity) =>
        new { type = "settings", humor, honesty, brevity };

    /// <summary>Cancel one timer or reminder.</summary>
    public static object CancelTimer(string id) => new { type = "cancel_timer", id };

    /// <summary>Stop a ringing alarm everywhere.</summary>
    public static object Dismiss() => new { type = "dismiss" };

    #endregion
}
