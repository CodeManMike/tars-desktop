namespace TarsClient.Services;

/// <summary>The outcome of one synthesis request.</summary>
/// <param name="Ok">It finished with audio.</param>
/// <param name="Started">At least one chunk played (so a fallback would talk over it).</param>
/// <param name="FirstAudioMs">Time to the first chunk.</param>
/// <param name="Samples">The processed audio, when the caller asked to collect it.</param>
public sealed record SpeechResult(bool Ok, bool Started, long FirstAudioMs, float[]? Samples);
