using System.Net;
using System.Net.Sockets;

namespace TarsClient.ViewModels;

/// <summary>SERVER tab: the server's <c>.env</c> through the admin API.</summary>
public sealed partial class SettingsViewModel
{
    #region Properties

    /// <summary>The server's config fields.</summary>
    public ObservableCollection<ConfigRow> ConfigRows { get; } = [];

    /// <summary>Saved changes are waiting for a server restart.</summary>
    [ObservableProperty]
    public partial bool RestartRequired { get; set; }

    #endregion

    #region Commands

    [RelayCommand]
    private async Task LoadConfig()
    {
        Say("loading config…");
        try
        {
            var config = await _main.Admin.GetConfigAsync();
            ConfigRows.Clear();
            foreach (var field in config.Fields)
            {
                ConfigRows.Add(new ConfigRow { Field = field, Original = field.Value, Value = field.Value, ServerPending = field.PendingRestart });
            }
            RestartRequired = config.RestartRequired;
            Say($"{config.Fields.Count} fields{(config.RestartRequired ? " · RESTART REQUIRED" : "")}");
        }
        catch (Exception ex) when (IsAdminFailure(ex))
        {
            Say(ex.Message);
        }
    }

    /// <summary>Enter on a row: bools toggle, choices cycle, strings and secrets open an inline editor.</summary>
    [RelayCommand]
    private void EditRow(ConfigRow row)
    {
        switch (row)
        {
            case { IsBool: true }:
                row.Value = row.Value is "1" or "true" or "True" ? "0" : "1";
                break;
            case { IsChoice: true }:
                row.Value = row.Choices[(Array.IndexOf(row.Choices, row.Value) + 1) % row.Choices.Length];
                break;
            default:
                row.EditText = row.Secret ? "" : row.Value;
                row.IsEditing = true;
                break;
        }
    }

    /// <summary>Closes the inline editor; an empty secret keeps the current one.</summary>
    [RelayCommand]
    private void CommitRow(ConfigRow row)
    {
        row.IsEditing = false;
        if (row.Secret && row.EditText.Length == 0) return;
        row.Value = row.EditText.Trim();
    }

    [RelayCommand]
    private void CancelRow(ConfigRow row) => row.IsEditing = false;

    [RelayCommand]
    private Task ApplyConfig() => SendConfig(restart: false);

    [RelayCommand]
    private Task ApplyRestart() => SendConfig(restart: true);

    [RelayCommand]
    private async Task RestartServer()
    {
        if (!await ConfirmAsync("Restart the TARS server? Every client drops for ~10 s.")) return;
        try
        {
            await _main.Admin.RestartAsync();
            Say("restarting…");
        }
        catch (Exception ex) when (IsAdminFailure(ex))
        {
            Say(ex.Message);
        }
    }

    #endregion

    #region Private Methods

    /// <summary>The ways an admin API call fails that we show rather than crash on.</summary>
    private static bool IsAdminFailure(Exception ex) =>
        ex is AdminException or HttpRequestException or TaskCanceledException or JsonException;

    private async Task SendConfig(bool restart)
    {
        var changes = ConfigRows.Where(r => r.Changed).ToDictionary(r => r.Key, r => r.Value);
        if (changes.Count == 0 && !restart)
        {
            Say("nothing changed");
            return;
        }
        if (!await ConfirmRiskyChangesAsync(changes)) return;

        Say(restart ? "applying + restarting…" : "applying…");
        try
        {
            if (changes.Count > 0) await _main.Admin.PatchConfigAsync(changes, restart);
            else await _main.Admin.RestartAsync();
            Say(restart ? "server restarting: NO CARRIER expected, back in ~10 s" : "saved: fields marked PENDING RESTART");
            if (!restart)
            {
                await LoadConfig();
                return;
            }
            ConfigRows.Clear();
            await Task.Delay(12000);
            if (TabName == "SERVER" && _main.SettingsOpen) await LoadConfig();
        }
        catch (Exception ex) when (IsAdminFailure(ex))
        {
            Say(ex.Message);
        }
    }

    /// <summary>
    /// A new TARS_KEY is stored here first, so we don't lock ourselves out; dropping this PC from ALLOWED_CLIENTS needs
    /// a yes.
    /// </summary>
    private async Task<bool> ConfirmRiskyChangesAsync(Dictionary<string, string> changes)
    {
        if (changes.TryGetValue("TARS_KEY", out var newKey))
        {
            if (!await ConfirmAsync("Change TARS_KEY? The client stores the new key first, then updates the server.")) return false;
            AccessKey = newKey;
        }
        if (changes.TryGetValue("ALLOWED_CLIENTS", out var allowed) && !IncludesThisPc(allowed))
        {
            return await ConfirmAsync("ALLOWED_CLIENTS no longer lists this PC. It will need the access key to connect. Continue?");
        }
        return true;
    }

    private static bool IncludesThisPc(string list)
    {
        var entries = list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                          .Select(e => e.ToLowerInvariant())
                          .ToHashSet();
        var host = Dns.GetHostName().ToLowerInvariant();
        if (entries.Contains(host) || entries.Contains(host + ".lan")) return true;
        try
        {
            return Dns.GetHostAddresses(host).Any(a => entries.Contains(a.ToString()));
        }
        catch (SocketException)
        {
            return false;
        }
    }

    #endregion
}
