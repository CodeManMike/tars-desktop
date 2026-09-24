namespace TarsClient.Models;

/// <summary>A finished, transcribed utterance from the local speech-to-text.</summary>
/// <param name="Text">What was said.</param>
/// <param name="Source"><c>vad</c> (hands-free) or <c>ptt</c>.</param>
/// <param name="SpeechMs">Voiced time.</param>
/// <param name="DurationMs">Clip length, including pre-roll and trailing silence.</param>
/// <param name="SttMs">Time Whisper took.</param>
/// <param name="LogProb">Mean segment log-probability.</param>
/// <param name="NoSpeechProb">Highest segment no-speech probability.</param>
/// <param name="Model">The Whisper model that transcribed it.</param>
public sealed record Utterance(string Text, string Source, int SpeechMs, int DurationMs, int SttMs,
                               double LogProb, double NoSpeechProb, string Model);
