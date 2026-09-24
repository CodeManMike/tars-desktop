namespace TarsClient.Services;

/// <summary>Where the local voice is at, as the title bar and VOICE tab show it.</summary>
public enum VoiceState
{
    /// <summary>The venv or sidecar script is missing.</summary>
    NotInstalled,

    /// <summary>The sidecar process is starting.</summary>
    Starting,

    /// <summary>Ready.</summary>
    Online,

    /// <summary>A GPU model is loading; Kokoro covers replies meanwhile.</summary>
    Warming,

    /// <summary>A game has the GPU; Kokoro speaks on the CPU.</summary>
    GameMode,

    /// <summary>The sidecar isn't running; the Windows voice covers replies.</summary>
    Offline,
}
