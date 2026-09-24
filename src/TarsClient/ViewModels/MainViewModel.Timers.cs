namespace TarsClient.ViewModels;

/// <summary>Timers, reminders, alarms and the volume meter.</summary>
public sealed partial class MainViewModel
{
    #region Fields

    private const int MaxChimes = 8;

    private double _clockOffset;
    private int _chimes;

    #endregion

    #region Properties

    /// <summary>Volume in percent, snapped to 5 % steps (0 to 150).</summary>
    public double VolumePercent
    {
        get => Math.Round(S.Volume * 100);
        set
        {
            S.Volume = Math.Clamp(Math.Round(value / 5) * 5, 0, 150) / 100.0;
            Playback.Volume = S.Volume;
            Store.Save();
            UpdateVolumeMeter();
            OnPropertyChanged();
        }
    }

    #endregion

    #region Public Methods

    /// <summary>Moves the volume by <paramref name="steps"/> × 5 %.</summary>
    public void NudgeVolume(int steps) => VolumePercent += steps * 5;

    #endregion

    #region Commands

    /// <summary>Esc, a click or the stop hotkey: silence and dismiss everywhere (the server then stops every client).</summary>
    [RelayCommand]
    public void Dismiss()
    {
        StopSpeech();
        StopAlarm(tellOthers: true);
    }

    [RelayCommand]
    private void CancelTimer(string id) => Server.SendJson(ClientMessages.CancelTimer(id));

    #endregion

    #region Private Methods

    private static double NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private void OnTimers(JsonElement m)
    {
        double serverNow = m.TryGetProperty("now", out var n) && n.TryGetDouble(out var nv) ? nv : NowSeconds();
        _clockOffset = serverNow - NowSeconds();
        Timers.Clear();
        if (m.TryGetProperty("timers", out var timers) && timers.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in timers.EnumerateArray())
            {
                Timers.Add(new TimerItem
                {
                    Id = Str(t, "id") ?? "",
                    Kind = Str(t, "kind") ?? "timer",
                    Label = Str(t, "label") ?? "",
                    Due = t.TryGetProperty("due", out var d) && d.TryGetDouble(out var due) ? due : 0,
                });
            }
        }
        HasTimers = Timers.Count > 0;
        RenderTimers();
        if (HasTimers) _timerTick.Start();
        else _timerTick.Stop();
    }

    private void RenderTimers()
    {
        double now = NowSeconds() + _clockOffset;
        foreach (var t in Timers)
        {
            t.Display = t.Kind == "timer" ? TimerDisplay(t, now) : ReminderDisplay(t);
        }
    }

    private static string TimerDisplay(TimerItem t, double now)
    {
        var left = TimeSpan.FromSeconds(Math.Max(0, Math.Round(t.Due - now)));
        var label = (t.Label is "" or "timer" ? "TIMER" : t.Label).ToUpperInvariant();
        return left.TotalHours >= 1
            ? $"{label} {(int)left.TotalHours}:{left:mm\\:ss}"
            : $"{label} {(int)left.TotalMinutes:00}:{left:ss}";
    }

    /// <summary>The server's clock is authoritative; we show the reminder in local time.</summary>
    private string ReminderDisplay(TimerItem t)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds((long)((t.Due - _clockOffset) * 1000)).ToLocalTime();
        return $"{at:HH:mm} {t.Label}";
    }

    private void RingAlarm(string text)
    {
        Voice.Stop();
        Playback.Stop();
        AlarmActive = true;
        FinishTyping();
        ReplyText = "⏰ " + text;
        Add(LineKind.Tars, "⏰ " + text);
        _chimes = 0;
        Playback.Chime();
        _chimeTimer.Stop();
        _chimeTimer.Start();
        _flashTimer.Start();
        ShowRequested?.Invoke(true);
        RecomputeStatus();
    }

    /// <summary>Every 10 s while ringing; gives up after <see cref="MaxChimes"/>.</summary>
    private void ChimeAgain()
    {
        if (++_chimes > MaxChimes)
        {
            StopAlarm(tellOthers: false);
            return;
        }
        Playback.Chime();
    }

    private void StopAlarm(bool tellOthers)
    {
        if (!AlarmActive) return;
        AlarmActive = false;
        _chimeTimer.Stop();
        _flashTimer.Stop();
        FlashOn = false;
        if (tellOthers) Server.SendJson(ClientMessages.Dismiss());
        AlarmStopped?.Invoke();
        RecomputeStatus();
    }

    private void UpdateVolumeMeter()
    {
        VolumeLevel = Math.Clamp(Math.Round(S.Volume * 9), 0, 9);
        VolumeHot = S.Volume > 1.0;
        VolumeText = $"{S.Volume * 100:0}%";
    }

    #endregion
}
