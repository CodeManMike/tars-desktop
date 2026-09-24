using System.IO.Compression;
using Microsoft.Win32;

namespace TarsSetup;

/// <summary>
/// Per-user install (no admin): %LOCALAPPDATA%\Programs\TARS, a Start menu (and optional desktop) shortcut, an
/// Apps &amp; Features entry, optional start-with-Windows and the optional local voice engine. Settings
/// (%APPDATA%\TARS) and the voice venv (%LOCALAPPDATA%\TARS) survive reinstalls.
/// </summary>
public static partial class Installer
{
    #region Fields

    /// <summary>The name in Apps &amp; Features.</summary>
    public const string AppName = "TARS Desktop";

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TARSDesktop";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    #endregion

    #region Properties

    /// <summary>Where the program files go.</summary>
    public static string InstallDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TARS");

    /// <summary>The client.</summary>
    public static string AppExe => Path.Combine(InstallDir, "TARS.exe");

    /// <summary>The copy of this setup that acts as the uninstaller.</summary>
    public static string UninstallerExe => Path.Combine(InstallDir, "TARS-Uninstall.exe");

    /// <summary>The voice engine's Python venv.</summary>
    public static string VenvDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TARS", "tts-venv");

    /// <summary>Settings, logs and voices.</summary>
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TARS");

    /// <summary>The voice engine is already installed.</summary>
    public static bool VoiceInstalled => File.Exists(Path.Combine(VenvDir, "Scripts", "python.exe"));

    /// <summary>TARS is already installed (this is an upgrade).</summary>
    public static bool IsInstalled => File.Exists(AppExe);

    /// <summary>This setup's version.</summary>
    public static string Version => typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private static string StartMenuDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TARS");

    private static string StartMenuLink => Path.Combine(StartMenuDir, "TARS.lnk");

    private static string StartMenuUninstallLink => Path.Combine(StartMenuDir, "Uninstall TARS.lnk");

    private static string LegacyStartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TARS.lnk");

    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "TARS.lnk");

    private static bool IsTempCopy => (Environment.ProcessPath ?? "").StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Public Methods

    /// <summary>Installs or upgrades, reporting progress through <paramref name="log"/>.</summary>
    public static async Task InstallAsync(InstallOptions options, Action<string> log)
    {
        log("STOPPING RUNNING TARS…");
        StopRunning();

        log($"EXTRACTING TO {InstallDir}");
        ClearInstallDir(log);
        await ExtractPayloadAsync(log);

        // A copy of this setup next to the app is the uninstaller: named TARS-Uninstall.exe, it opens in uninstall mode.
        var self = Environment.ProcessPath!;
        if (!string.Equals(self, UninstallerExe, StringComparison.OrdinalIgnoreCase)) File.Copy(self, UninstallerExe, true);

        log("SHORTCUTS");
        CreateShortcuts(options.DesktopShortcut);

        log("REGISTERING WITH APPS & FEATURES");
        RegisterUninstaller();
        SetStartWithWindows(options.StartWithWindows);

        if (options.InstallVoice)
        {
            log("INSTALLING LOCAL VOICE ENGINE (≈4.5 GB, first time only)…");
            int code = await RunPowerShellAsync(Path.Combine(InstallDir, "tts-sidecar", "install.ps1"), log);
            log(code == 0 ? "VOICE ENGINE READY" : $"VOICE ENGINE INSTALL FAILED ({code}): TARS falls back to the Windows voice; retry from VOICE → [INSTALL]");
        }

        log("DONE.");
        // Through Explorer, so TARS (and its voice sidecar) never inherit this process's tree or job.
        if (options.Launch) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppExe}\"") { UseShellExecute = false });
    }

    /// <summary>Removes TARS; settings and the voice engine go too only when <paramref name="removeData"/> is set.</summary>
    public static async Task UninstallAsync(bool removeData, Action<string> log)
    {
        log("STOPPING RUNNING TARS…");
        StopRunning();
        await Task.Delay(300);

        RemoveShortcuts();
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) run?.DeleteValue("TARS", false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        log("SHORTCUTS AND REGISTRY ENTRIES REMOVED");

        if (removeData) RemoveData(log);
        else log($"KEPT SETTINGS ({DataDir}) AND VOICE ENGINE ({VenvDir})");

        await RemoveInstallDirAsync(log);
        if (IsTempCopy) DeleteSelfLater();
        log("TARS DESKTOP IS UNINSTALLED. GOODBYE.");
    }

    /// <summary>
    /// A running exe can't delete its own folder: when the uninstaller starts from inside the install folder, it copies
    /// itself to %TEMP% and relaunches from there. Returns true when the caller should exit.
    /// </summary>
    public static bool RelaunchFromTempIfInside(string[] args)
    {
        var self = Environment.ProcessPath;
        if (self == null || !self.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase)) return false;
        var temp = Path.Combine(Path.GetTempPath(), $"TARS-Uninstall-{Guid.NewGuid():N}.exe");
        File.Copy(self, temp, true);
        var psi = new ProcessStartInfo(temp) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
        psi.ArgumentList.Add("--uninstall");
        foreach (var arg in args.Where(a => !a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))) psi.ArgumentList.Add(arg);
        Process.Start(psi);
        return true;
    }

    #endregion

    #region Private Methods

    /// <summary>Replaces program files only; the venv and settings live elsewhere.</summary>
    private static void ClearInstallDir(Action<string> log)
    {
        if (!Directory.Exists(InstallDir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(InstallDir))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, true);
                else File.Delete(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"  ! could not remove {Path.GetFileName(entry)}: {ex.Message}");
            }
        }
    }

    private static async Task ExtractPayloadAsync(Action<string> log)
    {
        await using var payload = typeof(Installer).Assembly.GetManifestResourceStream("payload.zip")
                                  ?? throw new InvalidOperationException("This setup was built without a payload (build/build-installer.ps1).");
        Directory.CreateDirectory(InstallDir);
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        int done = 0, total = zip.Entries.Count;
        foreach (var entry in zip.Entries)
        {
            var dest = Path.GetFullPath(Path.Combine(InstallDir, entry.FullName));
            if (!dest.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase)) continue;   // zip-slip guard
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(dest);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await Task.Run(() => entry.ExtractToFile(dest, true));
            if (++done % 40 == 0 || done == total) log($"  {done}/{total} FILES");
        }
    }

    private static void CreateShortcuts(bool desktop)
    {
        DeleteIfExists(LegacyStartMenuLink);
        Directory.CreateDirectory(StartMenuDir);
        CreateShortcut(StartMenuLink, AppExe, "TARS desktop client");
        CreateShortcut(StartMenuUninstallLink, UninstallerExe, "Remove TARS Desktop");
        if (desktop) CreateShortcut(DesktopLink, AppExe, "TARS desktop client");
        else DeleteIfExists(DesktopLink);
    }

    private static void RemoveShortcuts()
    {
        foreach (var link in (string[])[StartMenuLink, StartMenuUninstallLink, LegacyStartMenuLink, DesktopLink]) DeleteIfExists(link);
        if (Directory.Exists(StartMenuDir) && !Directory.EnumerateFileSystemEntries(StartMenuDir).Any()) DeleteIfExists(StartMenuDir);
    }

    private static void RegisterUninstaller()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "Michael");
        key.SetValue("DisplayIcon", AppExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{UninstallerExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{UninstallerExe}\" --uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(DirSize(InstallDir) / 1024), RegistryValueKind.DWord);
    }

    private static void SetStartWithWindows(bool enabled)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) run.SetValue("TARS", $"\"{AppExe}\" --minimized");
            else run.DeleteValue("TARS", false);
        }
        SetClientStartupPreference(enabled);
    }

    private static void RemoveData(Action<string> log)
    {
        foreach (var dir in (string[])[DataDir, Path.GetDirectoryName(VenvDir)!])
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                Directory.Delete(dir, true);
                log($"REMOVED {dir}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"  ! {dir}: {ex.Message}");
            }
        }
    }

    /// <summary>We run from a %TEMP% copy (see <see cref="RelaunchFromTempIfInside"/>), so the install folder can go now.</summary>
    private static async Task RemoveInstallDirAsync(Action<string> log)
    {
        for (int attempt = 1; Directory.Exists(InstallDir); attempt++)
        {
            try
            {
                Directory.Delete(InstallDir, true);
                log($"REMOVED {InstallDir}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                log($"  retrying ({ex.Message})");
                await Task.Delay(1000);
                StopRunning();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"  ! {InstallDir}: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>Tidies up this temporary copy after it exits.</summary>
    private static void DeleteSelfLater()
    {
        var self = Environment.ProcessPath!;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & del /q \"{self}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    #endregion
}
