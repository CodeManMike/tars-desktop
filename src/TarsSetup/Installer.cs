using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Microsoft.Win32;

namespace TarsSetup;

/// <summary>
/// Per-user install (no admin): %LOCALAPPDATA%\Programs\TARS, Start menu (+ optional desktop) shortcut,
/// an Apps &amp; Features entry, optional start-with-Windows, optional local voice engine.
/// Settings (%APPDATA%\TARS) and the voice venv (%LOCALAPPDATA%\TARS) survive reinstalls.
/// </summary>
public static class Installer
{
    public const string AppName = "TARS Desktop";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TARSDesktop";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string InstallDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TARS");
    public static string AppExe => Path.Combine(InstallDir, "TARS.exe");
    public static string VenvDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TARS", "tts-venv");
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TARS");
    static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TARS.lnk");
    static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "TARS.lnk");

    public static bool VoiceInstalled => File.Exists(Path.Combine(VenvDir, "Scripts", "python.exe"));
    public static bool IsInstalled => File.Exists(AppExe);
    public static string Version => typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public sealed record Options(bool StartWithWindows, bool DesktopShortcut, bool InstallVoice, bool Launch);

    public static async Task InstallAsync(Options o, Action<string> log)
    {
        log("STOPPING RUNNING TARS…");
        StopRunning();

        log($"EXTRACTING TO {InstallDir}");
        await using (var payload = typeof(Installer).Assembly.GetManifestResourceStream("payload.zip")
                                   ?? throw new InvalidOperationException("This setup was built without a payload (build/build-installer.ps1)."))
        {
            // Replace program files only; the tts-venv and settings live elsewhere.
            if (Directory.Exists(InstallDir))
                foreach (var entry in Directory.EnumerateFileSystemEntries(InstallDir))
                {
                    try { if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry); }
                    catch (Exception ex) { log($"  ! could not remove {Path.GetFileName(entry)}: {ex.Message}"); }
                }
            Directory.CreateDirectory(InstallDir);
            using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
            int n = 0, total = zip.Entries.Count;
            foreach (var entry in zip.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(InstallDir, entry.FullName));
                if (!dest.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase)) continue;   // zip-slip guard
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await Task.Run(() => entry.ExtractToFile(dest, true));
                if (++n % 40 == 0 || n == total) log($"  {n}/{total} FILES");
            }
        }

        // Keep a copy of this setup next to the app: it's the uninstaller.
        var self = Environment.ProcessPath!;
        var uninstaller = Path.Combine(InstallDir, "TARS-Setup.exe");
        if (!string.Equals(self, uninstaller, StringComparison.OrdinalIgnoreCase)) File.Copy(self, uninstaller, true);

        log("SHORTCUTS");
        CreateShortcut(StartMenuLink, AppExe, "TARS desktop client");
        if (o.DesktopShortcut) CreateShortcut(DesktopLink, AppExe, "TARS desktop client");
        else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);

        log("REGISTERING WITH APPS & FEATURES");
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", AppName);
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "Michael");
            k.SetValue("DisplayIcon", AppExe);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(DirSize(InstallDir) / 1024), RegistryValueKind.DWord);
        }

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (o.StartWithWindows) run.SetValue("TARS", $"\"{AppExe}\" --minimized");
            else run.DeleteValue("TARS", false);
        }
        SetClientStartupPreference(o.StartWithWindows);

        if (o.InstallVoice)
        {
            log("INSTALLING LOCAL VOICE ENGINE (≈4.5 GB, first time only)…");
            int code = await RunPowerShellAsync(Path.Combine(InstallDir, "tts-sidecar", "install.ps1"), log);
            log(code == 0 ? "VOICE ENGINE READY" : $"VOICE ENGINE INSTALL FAILED ({code}): TARS falls back to the Windows voice; retry from VOICE → [INSTALL]");
        }

        log("DONE.");
        if (o.Launch) Process.Start(new ProcessStartInfo(AppExe) { UseShellExecute = true, WorkingDirectory = InstallDir });
    }

    public static async Task UninstallAsync(bool removeData, Action<string> log)
    {
        log("STOPPING RUNNING TARS…");
        StopRunning();
        await Task.Delay(300);

        foreach (var link in new[] { StartMenuLink, DesktopLink })
            try { if (File.Exists(link)) File.Delete(link); } catch { }
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) run?.DeleteValue("TARS", false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        log("SHORTCUTS AND REGISTRY ENTRIES REMOVED");

        if (removeData)
        {
            foreach (var dir in new[] { DataDir, Path.GetDirectoryName(VenvDir)! })
                try { if (Directory.Exists(dir)) { Directory.Delete(dir, true); log($"REMOVED {dir}"); } }
                catch (Exception ex) { log($"  ! {dir}: {ex.Message}"); }
        }
        else log($"KEPT SETTINGS ({DataDir}) AND VOICE ENGINE ({VenvDir})");

        // This exe lives in the folder being removed: finish the job from a detached cmd after we exit.
        var cmd = $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        log("PROGRAM FILES WILL BE REMOVED WHEN THIS WINDOW CLOSES. GOODBYE.");
    }

    static void StopRunning()
    {
        foreach (var p in Process.GetProcessesByName("TARS"))
        {
            try
            {
                if (!p.CloseMainWindow() || !p.WaitForExit(1500)) p.Kill(true);
                p.WaitForExit(3000);
            }
            catch { }
        }
    }

    static void SetClientStartupPreference(bool startWithWindows)
    {
        // Keep client.json in agreement, so the client doesn't re-add (or drop) the Run entry on its next start.
        var path = Path.Combine(DataDir, "client.json");
        try
        {
            System.Text.Json.Nodes.JsonObject json = File.Exists(path)
                ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new()
                : new();
            json["startWithWindows"] = startWithWindows;
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(path, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    static void CreateShortcut(string link, string target, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic sc = shell.CreateShortcut(link);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.Description = description;
            sc.IconLocation = target + ",0";
            sc.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    static async Task<int> RunPowerShellAsync(string script, Action<string> log)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script },
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log("  " + e.Data.Trim()); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && !e.Data.Contains("Warning")) log("  ! " + e.Data.Trim()); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    static long DirSize(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
}
