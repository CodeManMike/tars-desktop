namespace TarsClient.ViewModels;

/// <summary>
/// The settings screen: one tab per concern (personality, voice, audio, system, server config, server files, status).
/// Each tab lives in its own partial file; this one holds the tab machinery, the confirm prompt and PERSONALITY.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    #region Fields

    /// <summary>The tabs, in order.</summary>
    public static readonly string[] TabNames = ["PERSONALITY", "VOICE", "AUDIO", "SYSTEM", "SERVER", "FILES", "STATUS"];

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _statusPoll;
    private bool _fromServer;
    private TaskCompletionSource<bool>? _confirm;

    #endregion

    #region Constructor

    /// <summary>Creates the settings screen for <paramref name="main"/>.</summary>
    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        _statusPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusPoll.Tick += async (_, _) => await RefreshStatus();
        LabRows = BuildLabRows();
    }

    #endregion

    #region Properties

    /// <summary>The selected tab.</summary>
    [ObservableProperty]
    public partial int TabIndex { get; set; }

    /// <summary>The status line under the tabs.</summary>
    [ObservableProperty]
    public partial string Message { get; set; } = "";

    /// <summary>The yes/no prompt is showing.</summary>
    [ObservableProperty]
    public partial bool ConfirmVisible { get; set; }

    /// <summary>The yes/no prompt's question.</summary>
    [ObservableProperty]
    public partial string ConfirmText { get; set; } = "";

    /// <summary>The selected tab's name.</summary>
    public string TabName => TabNames[TabIndex];

    /// <summary>Humor, 0 to 100 (shared with every client via the server).</summary>
    [ObservableProperty]
    public partial double Humor { get; set; } = 75;

    /// <summary>Honesty, 0 to 100.</summary>
    [ObservableProperty]
    public partial double Honesty { get; set; } = 95;

    /// <summary>Brevity, 0 to 100.</summary>
    [ObservableProperty]
    public partial double Brevity { get; set; } = 85;

    private ClientSettings S => _main.S;

    #endregion

    #region Public Methods

    /// <summary>Moves one tab left (-1) or right (+1), wrapping.</summary>
    public void NextTab(int direction) => TabIndex = (TabIndex + direction + TabNames.Length) % TabNames.Length;

    /// <summary>The screen opened: refresh what may have changed while it was closed.</summary>
    public void OnOpened()
    {
        PttKey = S.PttKey;
        StopHotkey = S.StopHotkey;
        RefreshDevices();
        RefreshVoice();
        _ = OnTabShown();
    }

    /// <summary>The screen closed: stop polling.</summary>
    public void OnClosed() => _statusPoll.Stop();

    /// <summary>Shows a yes/no prompt in the settings footer and waits for the answer.</summary>
    public Task<bool> ConfirmAsync(string text)
    {
        _confirm?.TrySetResult(false);
        _confirm = new TaskCompletionSource<bool>();
        ConfirmText = text;
        ConfirmVisible = true;
        return _confirm.Task;
    }

    /// <summary>The personality broadcast from the server; applied without echoing it back.</summary>
    public void FromServer(JsonElement settings)
    {
        _fromServer = true;
        try
        {
            if (settings.TryGetProperty("humor", out var h) && h.TryGetDouble(out var humor)) Humor = humor;
            if (settings.TryGetProperty("honesty", out var o) && o.TryGetDouble(out var honesty)) Honesty = honesty;
            if (settings.TryGetProperty("brevity", out var b) && b.TryGetDouble(out var brevity)) Brevity = brevity;
        }
        finally
        {
            _fromServer = false;
        }
    }

    #endregion

    #region Commands

    [RelayCommand]
    private void ConfirmYes() => Answer(true);

    [RelayCommand]
    private void ConfirmNo() => Answer(false);

    #endregion

    #region Private Methods

    private void Answer(bool yes)
    {
        ConfirmVisible = false;
        _confirm?.TrySetResult(yes);
    }

    private void Say(string text) => Message = text;

    private async Task OnTabShown()
    {
        _statusPoll.Stop();
        if (!_main.SettingsOpen) return;
        switch (TabName)
        {
            case "SERVER" when ConfigRows.Count == 0:
                await LoadConfig();
                break;
            case "FILES" when Files.Count == 0:
                await LoadFiles();
                break;
            case "STATUS":
                await RefreshStatus();
                _statusPoll.Start();
                break;
        }
    }

    /// <summary>Saves the settings and raises <c>PropertyChanged</c> for the caller.</summary>
    private void Save([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        _main.Store.Save();
        OnPropertyChanged(name);
    }

    partial void OnTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(TabName));
        Message = "";
        _ = OnTabShown();
    }

    partial void OnHumorChanged(double value) => PersonalityMoved();

    partial void OnHonestyChanged(double value) => PersonalityMoved();

    partial void OnBrevityChanged(double value) => PersonalityMoved();

    private void PersonalityMoved()
    {
        if (!_fromServer) _main.PersonalityChanged();
    }

    #endregion
}
