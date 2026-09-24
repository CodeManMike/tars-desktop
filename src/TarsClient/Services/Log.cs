namespace TarsClient.Services;

/// <summary>A tiny append-only log at <c>%APPDATA%\TARS\client.log</c>, rolled at 1 MB.</summary>
public static class Log
{
    #region Fields

    private const long RollBytes = 1_000_000;
    private static readonly object Gate = new();
    private static readonly string PathName = Path.Combine(SettingsStore.Folder, "client.log");

    #endregion

    #region Public Methods

    /// <summary>Appends a timestamped line. Logging must never take the app down, so failures are swallowed.</summary>
    public static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.Folder);
                var file = new FileInfo(PathName);
                if (file.Exists && file.Length > RollBytes) File.Move(PathName, PathName + ".1", true);
                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    #endregion
}
