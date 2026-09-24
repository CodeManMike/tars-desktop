using System.Text.RegularExpressions;

namespace TarsClient.Services;

/// <summary>Wake-name matching for the interim, client-side gate (used only when the server can't judge <c>utterance</c> itself).</summary>
public static partial class WakeWords
{
    #region Fields

    [GeneratedRegex(@"[^a-z0-9']+")]
    private static partial Regex WordSplitter();

    #endregion

    #region Public Methods

    /// <summary>Whether <paramref name="text"/> contains any of <paramref name="wakeWords"/> as a whole word (case-insensitive).</summary>
    public static bool Mentions(string text, IReadOnlyCollection<string> wakeWords) =>
        WordSplitter().Split(text.ToLowerInvariant()).Any(wakeWords.Contains);

    /// <summary>Builds the lower-cased wake list from the server's <c>WAKE_WORDS</c> (comma-separated) plus the assistant's name.</summary>
    public static string[] Parse(string? wakeWords, string? assistantName) =>
        (wakeWords ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                         .Append(string.IsNullOrWhiteSpace(assistantName) ? "tars" : assistantName)
                         .Select(w => w.ToLowerInvariant())
                         .Distinct()
                         .ToArray();

    #endregion
}
