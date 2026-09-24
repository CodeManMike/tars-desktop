using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarsSetup;

/// <summary>Processes, shortcuts, files and the client's settings: the OS-facing helpers behind install and uninstall.</summary>
public static partial class Installer
{
    #region Private Methods

    /// <summary>Closes TARS and its voice sidecar (which normally exits with TARS; belt and braces).</summary>
    private static void StopRunning()
    {
        foreach (var process in Process.GetProcessesByName("TARS").Concat(SidecarProcesses()))
        {
            try
            {
                if (!process.CloseMainWindow() || !process.WaitForExit(1500)) process.Kill(true);
                process.WaitForExit(3000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // It exited on its own meanwhile, or isn't ours to stop.
            }
        }
    }

    /// <summary>python.exe processes running from our venv.</summary>
    private static IEnumerable<Process> SidecarProcesses()
    {
        foreach (var process in Process.GetProcessesByName("python"))
        {
            string? path;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                continue;
            }
            if (path != null && path.StartsWith(VenvDir, StringComparison.OrdinalIgnoreCase)) yield return process;
        }
    }

    /// <summary>Keeps client.json in agreement, so the client doesn't re-add (or drop) the Run entry on its next start.</summary>
    private static void SetClientStartupPreference(bool startWithWindows)
    {
        var path = Path.Combine(DataDir, "client.json");
        try
        {
            var json = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [] : [];
            json["startWithWindows"] = startWithWindows;
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // The client re-registers from its own setting on start; nothing is lost.
        }
    }

    private static void CreateShortcut(string link, string target, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = Path.GetDirectoryName(target);
            shortcut.Description = description;
            shortcut.IconLocation = target + ",0";
            shortcut.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover shortcut is harmless.
        }
    }

    private static async Task<int> RunPowerShellAsync(string script, Action<string> log)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) log("  " + e.Data.Trim());
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data) && !e.Data.Contains("Warning")) log("  ! " + e.Data.Trim());
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static long DirSize(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    #endregion
}
