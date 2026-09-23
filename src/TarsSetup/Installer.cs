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
    static string StartMenuDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TARS");
    static string StartMenuLink => Path.Combine(StartMenuDir, "TARS.lnk");
    static string StartMenuUninstallLink => Path.Combine(StartMenuDir, "Uninstall TARS.lnk");
    static string LegacyStartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TARS.lnk");
    public static string UninstallerExe => Path.Combine(InstallDir, "TARS-Uninstall.exe");
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

        // A copy of this setup next to the app is the uninstaller: named TARS-Uninstall.exe it opens in uninstall mode.
        var self = Environment.ProcessPath!;
        var uninstaller = UninstallerExe;
        if (!string.Equals(self, uninstaller, StringComparison.OrdinalIgnoreCase)) File.Copy(self, uninstaller, true);

        log("SHORTCUTS");
        if (File.Exists(LegacyStartMenuLink)) File.Delete(LegacyStartMenuLink);
        Directory.CreateDirectory(StartMenuDir);
        CreateShortcut(StartMenuLink, AppExe, "TARS desktop client");
        CreateShortcut(StartMenuUninstallLink, uninstaller, "Remove TARS Desktop");
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
            k.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --quiet");
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
        // Through Explorer, so TARS (and its voice sidecar) never inherit this process's tree or job.
        if (o.Launch) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppExe}\"") { UseShellExecute = false });
    }

    public static async Task UninstallAsync(bool removeData, Action<string> log)
    {
        log("STOPPING RUNNING TARS…");
        StopRunning();
        await Task.Delay(300);

        foreach (var link in new[] { StartMenuLink, StartMenuUninstallLink, LegacyStartMenuLink, DesktopLink })
            try { if (File.Exists(link)) File.Delete(link); } catch { }
        try { if (Directory.Exists(StartMenuDir) && !Directory.EnumerateFileSystemEntries(StartMenuDir).Any()) Directory.Delete(StartMenuDir); } catch { }
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

        // We run from a %TEMP% copy (see RelaunchFromTempIfInside), so the install folder can go right now.
        for (int attempt = 1; Directory.Exists(InstallDir); attempt++)
        {
            try { Directory.Delete(InstallDir, true); log($"REMOVED {InstallDir}"); }
            catch (Exception ex) when (attempt < 5) { log($"  retrying ({ex.Message})"); await Task.Delay(1000); StopRunning(); }
            catch (Exception ex) { log($"  ! {InstallDir}: {ex.Message}"); break; }
        }

        // Tidy up this temporary copy after it exits.
        if (IsTempCopy)
        {
            var self = Environment.ProcessPath!;
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & del /q \"{self}\"")
                { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }
        log("TARS DESKTOP IS UNINSTALLED. GOODBYE.");
    }

    static bool IsTempCopy => (Environment.ProcessPath ?? "").StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A running exe can't delete its own folder: when the uninstaller starts from inside the install folder,
    /// it copies itself to %TEMP% and relaunches from there. Returns true if the caller should exit.
    /// </summary>
    public static bool RelaunchFromTempIfInside(string[] args)
    {
        var self = Environment.ProcessPath;
        if (self == null || !self.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase)) return false;
        var temp = Path.Combine(Path.GetTempPath(), $"TARS-Uninstall-{Guid.NewGuid():N}.exe");
        File.Copy(self, temp, true);
        var psi = new ProcessStartInfo(temp) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
        psi.ArgumentList.Add("--uninstall");
        foreach (var a in args) if (!a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(a);
        Process.Start(psi);
        return true;
    }

    static void StopRunning()
    {
        foreach (var p in Process.GetProcessesByName("TARS").Concat(SidecarProcesses()))
        {
            try
            {
                if (!p.CloseMainWindow() || !p.WaitForExit(1500)) p.Kill(true);
                p.WaitForExit(3000);
            }
            catch { }
        }
    }

    /// <summary>The voice sidecar: python.exe from our venv (normally it exits with TARS; belt and braces).</summary>
    static IEnumerable<Process> SidecarProcesses()
    {
        foreach (var p in Process.GetProcessesByName("python"))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            if (path != null && path.StartsWith(VenvDir, StringComparison.OrdinalIgnoreCase)) yield return p;
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
