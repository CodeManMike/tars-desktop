namespace TarsClient.ViewModels;

/// <summary>Server → client messages (spec section 5).</summary>
public sealed partial class MainViewModel
{
    #region Private Methods

    private void OnMessage(string type, JsonElement m)
    {
        switch (type)
        {
            case "hello":
                OnHello(m);
                break;
            case "settings" when m.TryGetProperty("settings", out var settings):
                Settings.FromServer(settings);
                break;
            case "timers":
                OnTimers(m);
                break;
            case "status":
                _serverState = Str(m, "state") ?? "idle";
                RecomputeStatus();
                break;
            case "heard":
                OnHeard(m);
                break;
            case "user":
                _dropTurnAudio = false;
                _turnActive = true;
                BeginTurn(Str(m, "text") ?? "");
                break;
            case "delta":
                AppendReply(Str(m, "text") ?? "", raw: true);
                break;
            case "reply":
                AppendReply(Str(m, "text") ?? "", raw: false);
                break;
            case "tool" when S.ShowDetails:
                Add(LineKind.Meta, $"  · {Str(m, "name")}: {ToolArg(m)}");
                break;
            case "done":
                OnDone(m);
                break;
            case "alarm":
                RingAlarm(Str(m, "text") ?? "Alarm");
                break;
            case "stop":
                StopSpeech(fromServer: true);
                StopAlarm(tellOthers: false);
                break;
            case "error":
                Add(LineKind.Error, "ERR " + Str(m, "text"));
                break;
            case "client_ok":
                OnClientOk(m);
                break;
            case "say" when _dropTurnAudio && Str(m, "kind") != "alarm":
                break;   // a chunk of a turn that was stopped
            case "say":
                Voice.Say(Str(m, "speak") ?? Str(m, "text") ?? "", Str(m, "kind") ?? "reply");
                break;
        }
    }

    private void OnHello(JsonElement m)
    {
        _serverFeatures = m.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array
            ? features.EnumerateArray().Select(f => f.GetString() ?? "").ToArray()
            : [];
        AssistantName = (Str(m, "name") ?? "TARS").ToUpperInvariant();
        RecomputeStatus();
    }

    private void OnHeard(JsonElement m)
    {
        if (!S.ShowDetails) return;
        if (!m.TryGetProperty("accepted", out var accepted) || accepted.GetBoolean()) return;
        Add(LineKind.Meta, $"  · ignored: \"{Str(m, "text")}\" ({Str(m, "reason")})");
    }

    private void OnDone(JsonElement m)
    {
        _dropTurnAudio = false;
        _turnActive = false;
        FinishTyping();
        if (S.ShowDetails) Add(LineKind.Meta, "  · " + DoneLine(m));
        _serverState = "idle";
        _replyLine = null;
        RecomputeStatus();
    }

    private void OnClientOk(JsonElement m)
    {
        if (Str(m, "tts") != "local") Log.Write($"server acknowledged tts={Str(m, "tts")}");
        bool serverStt = Str(m, "stt") == "local";
        if (serverStt != _serverStt)
            Log.Write($"server stt={(serverStt ? "local (utterance protocol)" : "not supported: client-side gating")}");
        _serverStt = serverStt;
        Settings.RefreshStt();
    }

    private static string? Str(JsonElement m, string name)
    {
        if (!m.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    /// <summary>The one argument worth showing for a tool call.</summary>
    private static string ToolArg(JsonElement m)
    {
        if (!m.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object) return "";
        foreach (var key in (string[])["query", "command", "fact", "entity", "result"])
        {
            if (args.TryGetProperty(key, out var v)) return v.ToString();
        }
        return args.ToString();
    }

    private static string DoneLine(JsonElement m)
    {
        var bits = new List<string> { Str(m, "via") ?? "" };
        if (m.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            if (t.TryGetProperty("first_audio_ms", out var fa) && fa.TryGetDouble(out var firstAudio)) bits.Add($"voice {firstAudio / 1000:0.0} s");
            if (t.TryGetProperty("total_ms", out var to) && to.TryGetDouble(out var total)) bits.Add($"total {total / 1000:0.0} s");
        }
        return string.Join(" · ", bits.Where(b => b != ""));
    }

    #endregion
}
