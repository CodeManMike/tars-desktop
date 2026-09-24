namespace TarsClient.ViewModels;

/// <summary>One line of the voice lab, rendered through every engine.</summary>
public sealed class LabRow
{
    #region Properties

    /// <summary>What kind of line it is, e.g. <c>NUMBER</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The words.</summary>
    public required string Text { get; init; }

    /// <summary>One cell per engine.</summary>
    public required List<LabCell> Cells { get; init; }

    #endregion
}
