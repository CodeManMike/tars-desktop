namespace TarsSetup;

/// <summary>The choices on the install screen.</summary>
/// <param name="StartWithWindows">Add a Run entry that starts TARS minimized.</param>
/// <param name="DesktopShortcut">Put a shortcut on the desktop.</param>
/// <param name="InstallVoice">Install (or update) the local voice engine venv.</param>
/// <param name="Launch">Start TARS when done.</param>
public sealed record InstallOptions(bool StartWithWindows, bool DesktopShortcut, bool InstallVoice, bool Launch);
