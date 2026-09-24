namespace TarsClient.Services;

/// <summary>An admin API call failed; the message is already worded for the settings screen.</summary>
/// <param name="message">A short, user-facing reason such as <c>401 ACCESS KEY REJECTED</c>.</param>
public sealed class AdminException(string message) : Exception(message);
