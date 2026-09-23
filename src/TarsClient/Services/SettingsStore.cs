using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using TarsClient.Models;

namespace TarsClient.Services;

/// <summary>Loads and saves client.json; the access key is DPAPI-protected (CurrentUser) on disk.</summary>
public sealed class SettingsStore
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TARS");
    public static string FilePath { get; } = Path.Combine(Folder, "client.json");

    readonly DispatcherTimer _debounce;

    public ClientSettings Current { get; private set; } = new();

    public SettingsStore()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); SaveNow(); };
    }

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.ClientSettings) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write($"settings: unreadable ({ex.Message}); using defaults");
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            Current = new();
        }
        Current.AccessKey = Unprotect(Current.AccessKeyProtected);
    }

    /// <summary>Saves shortly after the last change (slider drags don't hammer the disk).</summary>
    public void Save() { _debounce.Stop(); _debounce.Start(); }

    public void SaveNow()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var plain = Current.AccessKey;
            Current.AccessKeyProtected = Protect(plain);
            Current.AccessKey = "";
            var json = JsonSerializer.Serialize(Current, SettingsJsonContext.Default.ClientSettings);
            Current.AccessKey = plain;
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { Log.Write($"settings: save failed: {ex.Message}"); }
    }

    static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    static string Unprotect(string blob)
    {
        if (string.IsNullOrEmpty(blob)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void ApplyStartWithWindows(bool enabled, bool minimized)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? "";
                key.SetValue("TARS", minimized ? $"\"{exe}\" --minimized" : $"\"{exe}\"");
            }
            else key.DeleteValue("TARS", false);
        }
        catch (Exception ex) { Log.Write($"startup: {ex.Message}"); }
    }
}
