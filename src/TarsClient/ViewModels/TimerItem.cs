namespace TarsClient.ViewModels;

/// <summary>A running timer or reminder from the server's <c>timers</c> broadcast.</summary>
public sealed partial class TimerItem : ObservableObject
{
    #region Properties

    /// <summary>The server's id, used to cancel it.</summary>
    public required string Id { get; init; }

    /// <summary><c>timer</c> or <c>reminder</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>What it's for.</summary>
    public required string Label { get; init; }

    /// <summary>When it fires, in server Unix seconds.</summary>
    public double Due { get; init; }

    /// <summary>The countdown or local time shown in the timer strip.</summary>
    [ObservableProperty]
    public partial string Display { get; set; } = "";

    #endregion
}
