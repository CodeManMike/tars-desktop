namespace TarsClient.ViewModels;

/// <summary>STATUS tab: the server's health and log tail, polled every 2 s while the tab shows.</summary>
public sealed partial class SettingsViewModel
{
    #region Fields

    private bool _polling;

    #endregion

    #region Properties

    /// <summary>The health summary block.</summary>
    [ObservableProperty]
    public partial string StatusSummary { get; set; } = "";

    /// <summary>The server's recent log lines.</summary>
    public ObservableCollection<string> LogLines { get; } = [];

    #endregion

    #region Private Methods

    private async Task RefreshStatus()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var status = await _main.Admin.GetStatusAsync(200);
            StatusSummary = Summarize(status);
            if (status.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.Array) UpdateLog(log);
        }
        catch (Exception ex) when (IsAdminFailure(ex) || ex is KeyNotFoundException or InvalidOperationException)
        {
            Say(ex.Message);
        }
        finally
        {
            _polling = false;
        }
    }

    private string Summarize(JsonElement status)
    {
        var uptime = TimeSpan.FromSeconds(status.TryGetProperty("uptime_s", out var u) ? u.GetDouble() : 0);
        var health = status.GetProperty("health");
        var loaded = status.TryGetProperty("ollama_loaded", out var ol) ? string.Join(", ", ol.EnumerateArray().Select(x => x.ToString())) : "-";
        var clients = status.TryGetProperty("clients", out var cl) && cl.ValueKind == JsonValueKind.Array
            ? string.Join(", ", cl.EnumerateArray().Select(ClientName))
            : "-";
        var timers = status.TryGetProperty("timers", out var tm) && tm.ValueKind == JsonValueKind.Array ? tm.GetArrayLength() : 0;
        return
            $"UPTIME ......... {(int)uptime.TotalHours}h {uptime:mm}m {uptime:ss}s\n" +
            $"LOCAL MODEL .... {Field(health, "model")} (loaded: {loaded})\n" +
            $"CLOUD .......... {Field(health, "cloud")} · stt {Field(health, "cloud_stt")} · cooldown {Field(status, "cloud_cooldown_s")} s\n" +
            $"HOME ASSISTANT . {Field(health, "ha")}\n" +
            $"CLIENTS ........ {(clients == "" ? "none" : clients)}\n" +
            $"TIMERS ......... {timers}\n" +
            $"LOCAL VOICE .... {VoiceStatus}";
    }

    private static string Field(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.ToString() : "-";

    private static string ClientName(JsonElement c) =>
        c.ValueKind == JsonValueKind.Object ? Field(c, "name") + "@" + Field(c, "ip") : c.ToString();

    /// <summary>Replaces the log only when its last line changed, so the list doesn't flicker.</summary>
    private void UpdateLog(JsonElement log)
    {
        var lines = log.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        if (LogLines.Count > 0 && LogLines[^1] == lines.LastOrDefault()) return;
        LogLines.Clear();
        foreach (var line in lines) LogLines.Add(line);
    }

    #endregion
}
