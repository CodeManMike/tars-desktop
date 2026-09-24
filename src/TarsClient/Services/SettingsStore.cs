using System.Security.Cryptography;
using Microsoft.Win32;

namespace TarsClient.Services;

/// <summary>Loads and saves <c>client.json</c>. The access key is sealed with DPAPI (CurrentUser) on disk.</summary>
public sealed class SettingsStore
{
    #region Fields

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly DispatcherTimer _debounce;

    #endregion

    #region Constructor

    /// <summary>Creates the store; <see cref="Save"/> writes 600 ms after the last change.</summary>
    public SettingsStore()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            SaveNow();
        };
    }

    #endregion

    #region Properties

    /// <summary><c>%APPDATA%\TARS</c>: settings, logs and voices.</summary>
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TARS");

    /// <summary><c>%APPDATA%\TARS\client.json</c>.</summary>
    public static string FilePath { get; } = Path.Combine(Folder, "client.json");

    /// <summary>The live settings.</summary>
    public ClientSettings Current { get; private set; } = new();

    #endregion

    #region Public Methods

    /// <summary>Reads <c>client.json</c>. An unreadable file is kept as <c>.bad</c> and we start from defaults.</summary>
    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.ClientSettings) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Write($"settings: unreadable ({ex.Message}); using defaults");
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch (IOException) { }
            Current = new();
        }

        Current.AccessKey = Unprotect(Current.AccessKeyProtected);
        Migrate(Current);
    }

    /// <summary>Saves shortly after the last change, so slider drags don't hammer the disk.</summary>
    public void Save()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Saves now (atomically, via a temp file).</summary>
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Write($"settings: save failed: {ex.Message}");
        }
    }

    /// <summary>Adds or removes TARS from the Windows Run key.</summary>
    public static void ApplyStartWithWindows(bool enabled, bool minimized)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (!enabled)
            {
                key.DeleteValue("TARS", false);
                return;
            }
            var exe = Environment.ProcessPath ?? "";
            key.SetValue("TARS", minimized ? $"\"{exe}\" --minimized" : $"\"{exe}\"");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Write($"startup: {ex.Message}");
        }
    }

    #endregion

    #region Private Methods

    /// <summary>Upgrades values that earlier versions defaulted differently.</summary>
    private static void Migrate(ClientSettings s)
    {
        // Mouse side buttons were dropped as PTT keys: they're used elsewhere.
        if (s.PttKey.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase)) s.PttKey = "RightCtrl";
        // 0.72 was the first voice-lock default and rejected the owner's relaxed speech (0.70–0.76 in real use).
        if (Math.Abs(s.Stt.SpeakerThreshold - 0.72) < 0.001) s.Stt.SpeakerThreshold = 0.67;
    }

    private static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var sealedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(sealedBytes);
    }

    private static string Unprotect(string blob)
    {
        if (string.IsNullOrEmpty(blob)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";   // sealed by another user or machine: we re-provision the key from the server
        }
    }

    #endregion
}
