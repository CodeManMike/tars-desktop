namespace TarsClient.Services;

/// <summary>Control messages we send to the speech sidecar's <c>/stt/stream</c> (see <c>tts-sidecar/stt.py</c>).</summary>
public static class SidecarMessages
{
    #region Public Methods

    /// <summary>
    /// Model, device and voice lock. We always send the "TARS" hotword: without it a short "TARS" comes out as
    /// "Charles"; unclear name-only transcripts are rejected downstream instead.
    /// </summary>
    public static object Config(string model, string device, string speaker, double threshold) =>
        new { type = "config", model, device, hotwords = "TARS", speaker, speaker_threshold = threshold };

    /// <summary>Push-to-talk bracket: everything in between is one utterance.</summary>
    public static object Ptt(bool start) => new { type = "ptt", state = start ? "start" : "end" };

    /// <summary>Drop any half-heard utterance (TARS started talking).</summary>
    public static object Reset() => new { type = "reset" };

    #endregion
}
