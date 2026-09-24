using System.Diagnostics;

namespace TarsClient.ViewModels;

/// <summary>SYSTEM tab: connection, startup, appearance, restart and uninstall.</summary>
public sealed partial class SettingsViewModel
{
    #region Properties

    /// <summary>The server's WebSocket URL.</summary>
    public string ServerUrl { get => S.ServerUrl; set { S.ServerUrl = value.Trim(); Save(); } }

    /// <summary>The access key (DPAPI-sealed on disk); a new one is tried at once.</summary>
    public string AccessKey
    {
        get => S.AccessKey;
        set
        {
            if (S.AccessKey == value.Trim()) return;
            S.AccessKey = value.Trim();
            Save();
            if (_main.Server.IsDenied || !_main.IsConnected) _main.Server.Restart();
        }
    }

    /// <summary>Start with Windows.</summary>
    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            S.StartWithWindows = value;
            Save();
            SettingsStore.ApplyStartWithWindows(value, S.StartMinimized);
        }
    }

    /// <summary>Start in the tray.</summary>
    public bool StartMinimized
    {
        get => S.StartMinimized;
        set
        {
            S.StartMinimized = value;
            Save();
            SettingsStore.ApplyStartWithWindows(S.StartWithWindows, value);
        }
    }

    /// <summary>Always on top.</summary>
    public bool Topmost
    {
        get => _main.Topmost;
        set
        {
            if (value != _main.Topmost) _main.TogglePinCommand.Execute(null);
            OnPropertyChanged();
        }
    }

    /// <summary>CRT scanlines.</summary>
    public bool Scanlines { get => S.Scanlines; set { S.Scanlines = value; Apply(); } }

    /// <summary>Phosphor glow.</summary>
    public bool Glow { get => S.Glow; set { S.Glow = value; Apply(); } }

    /// <summary>Terminal font size.</summary>
    public double FontSize { get => S.FontSize; set { S.FontSize = value; Apply(); } }

    /// <summary>Typewriter speed, characters per second.</summary>
    public double TypewriterCps { get => S.TypewriterCps; set { S.TypewriterCps = (int)value; Apply(); } }

    /// <summary>Show tool calls, timings and ignored speech in the scrollback.</summary>
    public bool ShowDetails { get => S.ShowDetails; set { S.ShowDetails = value; Apply(); } }

    /// <summary>Show a taskbar button.</summary>
    public bool ShowInTaskbar { get => S.ShowInTaskbar; set { S.ShowInTaskbar = value; Apply(); } }

    /// <summary>Bring the window up when TARS replies.</summary>
    public bool PopOnReply { get => S.PopOnReply; set { S.PopOnReply = value; Apply(); } }

    /// <summary>Clicks don't take focus from other apps.</summary>
    public bool NoActivate { get => S.NoActivate; set { S.NoActivate = value; Apply(); } }

    /// <summary>TARS speaks replies to typed messages.</summary>
    public bool SpeakTyped { get => S.SpeakTyped; set { S.SpeakTyped = value; Apply(); } }

    /// <summary>Background opacity, percent (20 to 100). Going below 100 the first time needs a restart.</summary>
    public double BackgroundOpacity
    {
        get => Math.Round(S.BackgroundOpacity * 100);
        set
        {
            bool wasOpaque = S.BackgroundOpacity >= 0.999;
            S.BackgroundOpacity = Math.Clamp(value, 20, 100) / 100;
            Apply();
            if (wasOpaque && S.BackgroundOpacity < 0.999) Say("transparency needs a restart: [RESTART TARS]");
        }
    }

    #endregion

    #region Commands

    [RelayCommand]
    private async Task Uninstall()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TARS");
        var exe = ((string[])[Path.Combine(dir, "TARS-Uninstall.exe"), Path.Combine(dir, "TARS-Setup.exe")]).FirstOrDefault(File.Exists);
        if (exe == null)
        {
            Say("no installed copy found (this looks like a dev build)");
            return;
        }
        if (!await ConfirmAsync("Uninstall TARS Desktop? TARS will close and the uninstaller will open.")) return;
        _main.Store.SaveNow();
        Process.Start(new ProcessStartInfo(exe, "--uninstall") { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        Application.Current.Shutdown();
    }

    /// <summary>Restarts TARS once this instance (and its single-instance mutex) is gone.</summary>
    [RelayCommand]
    private void RestartApp()
    {
        _main.Store.SaveNow();
        var exe = Environment.ProcessPath!;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        Application.Current.Shutdown();
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        Say("dialing…");
        try
        {
            var health = await _main.Admin.HealthAsync();
            bool llm = health.TryGetProperty("llm_ready", out var ready) && ready.GetBoolean();
            Say($"OK · {health.GetProperty("name")} · llm {(llm ? "ready" : "down")} · cloud {health.GetProperty("cloud")}");
        }
        catch (Exception ex) when (IsAdminFailure(ex) || ex is KeyNotFoundException)
        {
            Say(ex.Message);
        }
    }

    [RelayCommand]
    private async Task FetchKey()
    {
        var key = await _main.Admin.FetchKeyFromSpecAsync();
        if (key == null)
        {
            Say("could not fetch the key (is this PC allowlisted?)");
            return;
        }
        AccessKey = key;
        Say("access key fetched and sealed with DPAPI");
    }

    [RelayCommand]
    private void Reconnect()
    {
        _main.Server.Restart();
        Say("reconnecting…");
    }

    #endregion

    #region Private Methods

    private void Apply([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        _main.ApplyAppearance();
        Save(name);
    }

    #endregion
}
