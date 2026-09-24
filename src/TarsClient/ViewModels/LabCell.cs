namespace TarsClient.ViewModels;

/// <summary>One engine's take on one voice-lab line: its audio, time to first audio and your rating.</summary>
public sealed partial class LabCell : ObservableObject
{
    #region Properties

    /// <summary>The engine that rendered it.</summary>
    public required string Engine { get; init; }

    /// <summary>Index into the lab lines.</summary>
    public required int Line { get; init; }

    /// <summary>The rendered audio, once there is any.</summary>
    public float[]? Samples { get; set; }

    /// <summary>Time to first audio, or a progress mark.</summary>
    [ObservableProperty]
    public partial string Timing { get; set; } = "---";

    /// <summary>Your rating, 0 to 5.</summary>
    [ObservableProperty]
    public partial int Stars { get; set; }

    /// <summary>The rating as stars.</summary>
    public string StarText => new string('★', Stars) + new string('☆', 5 - Stars);

    #endregion

    #region Private Methods

    partial void OnStarsChanged(int value) => OnPropertyChanged(nameof(StarText));

    #endregion
}
