namespace TarsClient.Models;

/// <summary>How speech is recognised: locally through the sidecar, or by the server.</summary>
public sealed class SttSettings
{
    #region Recognition

    /// <summary><c>local</c>: Whisper on this PC (falls back to the server while the sidecar is down). <c>server</c>: stream audio as before.</summary>
    public string Mode { get; set; } = "local";

    /// <summary>The GPU model.</summary>
    public string Model { get; set; } = "large-v3-turbo";

    /// <summary>The CPU model while a game has the GPU.</summary>
    public string GameModel { get; set; } = "small.en";

    #endregion

    #region Voice lock

    /// <summary>Only the owner's voice starts hands-free turns; other voices are dropped before Whisper runs.</summary>
    public bool VoiceLock { get; set; }

    /// <summary>Path of the owner's voiceprint (<c>.npy</c>).</summary>
    public string Voiceprint { get; set; } = "";

    /// <summary>Minimum voiceprint similarity. Measured: owner 0.70–0.94, TV voices 0.49–0.65.</summary>
    public double SpeakerThreshold { get; set; } = 0.67;

    #endregion
}
