namespace TarsClient.ViewModels;

/// <summary>VOICE tab, continued: the voice lab renders the same lines through every engine for an A/B by ear.</summary>
public sealed partial class SettingsViewModel
{
    #region Fields

    private static readonly (string Kind, string Text)[] LabLines =
    [
        ("FACT", "Tau Ceti is eleven point nine light years from Earth."),
        ("NUMBER", "That comes to one thousand two hundred and forty seven rand."),
        ("TIMER", "Rice timer set for twelve minutes."),
        ("JOKE", "My humor setting is at seventy five percent. That was the funny part."),
        ("CORRECTION", "Actually, it's Nairobi. Mombasa is the one with the beach."),
        ("WEATHER", "Tomorrow, fog in the morning. High of nineteen, low of fourteen."),
        ("ALARM", "Pasta timer done. It is now, officially, al dente."),
        ("FILLER", "Checking."),
    ];

    private static readonly TimeSpan LabTimeout = TimeSpan.FromSeconds(90);

    private CancellationTokenSource? _labCts;

    #endregion

    #region Properties

    /// <summary>The lab grid: one row per line, one cell per engine.</summary>
    public List<LabRow> LabRows { get; }

    #endregion

    #region Commands

    /// <summary>Renders every line through every engine (without playing), noting time to first audio.</summary>
    [RelayCommand]
    private async Task LabRender()
    {
        _labCts?.Cancel();
        _labCts = new CancellationTokenSource();
        var ct = _labCts.Token;
        if (!_main.Voice.Healthy)
        {
            Say("voice sidecar offline");
            return;
        }
        foreach (var engine in Engines)
        {
            foreach (var row in LabRows)
            {
                if (ct.IsCancellationRequested) return;
                Say($"rendering {engine}: {row.Kind}");
                await RenderCellAsync(row.Cells.First(c => c.Engine == engine), row.Text, ct);
            }
        }
        Say("lab rendered: play and rate");
    }

    [RelayCommand]
    private async Task LabPlay(LabCell cell)
    {
        _main.Playback.Stop();
        if (cell.Samples == null) await RenderCellAsync(cell, LabRows[cell.Line].Text, CancellationToken.None);
        if (cell.Samples != null) _main.Playback.Enqueue(cell.Samples);
    }

    /// <summary>Cycles the cell's stars (0 to 5), saves them, and shows each engine's total.</summary>
    [RelayCommand]
    private void LabRate(LabCell cell)
    {
        cell.Stars = cell.Stars >= 5 ? 0 : cell.Stars + 1;
        S.Tts.LabRatings[$"{cell.Engine}|{cell.Line}"] = cell.Stars;
        _main.Store.Save();
        var totals = Engines
            .Select(engine => (Engine: engine, Stars: LabRows.Sum(r => r.Cells.First(c => c.Engine == engine).Stars)))
            .OrderByDescending(t => t.Stars);
        Say("totals: " + string.Join(" · ", totals.Select(t => $"{t.Engine} {t.Stars}")));
    }

    #endregion

    #region Private Methods

    private List<LabRow> BuildLabRows() => LabLines.Select((line, i) => new LabRow
    {
        Kind = line.Kind,
        Text = line.Text,
        Cells = Engines.Select(engine => new LabCell
        {
            Engine = engine,
            Line = i,
            Stars = S.Tts.LabRatings.GetValueOrDefault($"{engine}|{i}"),
        }).ToList(),
    }).ToList();

    private async Task RenderCellAsync(LabCell cell, string text, CancellationToken ct)
    {
        cell.Timing = "…";
        var result = await _main.Voice.StreamAsync(cell.Engine, text, 1.0, ct, LabTimeout, collect: true, play: false);
        cell.Samples = result.Samples;
        cell.Timing = result.Ok ? $"{result.FirstAudioMs} ms" : "FAIL";
    }

    #endregion
}
