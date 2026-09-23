using System.IO;

namespace TarsClient.Services;

/// <summary>Tiny append-only log at %APPDATA%\TARS\client.log (rolled at 1 MB).</summary>
public static class Log
{
    static readonly object Gate = new();
    static readonly string PathName = Path.Combine(SettingsStore.Folder, "client.log");

    public static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.Folder);
                var fi = new FileInfo(PathName);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(PathName, PathName + ".1", true);
                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
