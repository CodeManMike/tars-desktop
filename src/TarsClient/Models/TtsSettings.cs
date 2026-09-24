namespace TarsClient.Models;

/// <summary>How TARS's voice is generated on this PC.</summary>
public sealed class TtsSettings
{
    #region Engine

    /// <summary>The local voice sidecar.</summary>
    public string SidecarUrl { get; set; } = "http://127.0.0.1:8880";

    /// <summary><c>turbo</c> (GPU, default), <c>chatterbox</c> (GPU, slower) or <c>kokoro</c> (CPU).</summary>
    public string Engine { get; set; } = "turbo";

    /// <summary>Reference clip TARS's voice is cloned from; empty means the default rendered locally.</summary>
    public string ReferenceClip { get; set; } = "";

    /// <summary>Free the GPU after this many idle minutes (0 means never).</summary>
    public int IdleUnloadMinutes { get; set; } = 10;

    /// <summary>Switch to the CPU voice while a game has the GPU.</summary>
    public bool GameMode { get; set; } = true;

    #endregion

    #region Delivery

    /// <summary>Emotion intensity (chatterbox only).</summary>
    public double Exaggeration { get; set; } = 0.30;

    /// <summary>Guidance / pacing (chatterbox only).</summary>
    public double Pace { get; set; } = 0.35;

    /// <summary>Speaking rate multiplier.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Sampling temperature: lower is steadier and flatter.</summary>
    public double Temperature { get; set; } = 0.7;

    #endregion

    #region TARS FX

    /// <summary>Apply TARS FX (slight pitch drop plus a metallic speaker colouring) on the client.</summary>
    public bool Fx { get; set; } = true;

    /// <summary>Varispeed: 1.0 is off, 0.95 is a touch deeper and slower.</summary>
    public double FxPitch { get; set; } = 0.95;

    /// <summary>Metallic ring (comb gain), 0–0.5.</summary>
    public double FxRing { get; set; } = 0.22;

    #endregion

    #region Voice lab

    /// <summary>Star ratings from the voice lab, keyed <c>engine|line</c>.</summary>
    public Dictionary<string, int> LabRatings { get; set; } = [];

    #endregion
}
