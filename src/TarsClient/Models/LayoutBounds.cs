namespace TarsClient.Models;

/// <summary>Window bounds for each layout, as <c>[left, top, width, height]</c> in device-independent pixels.</summary>
public sealed class LayoutBounds
{
    #region Properties

    /// <summary>The compact strip.</summary>
    public double[] Compact { get; set; } = [1500, 60, 380, 150];

    /// <summary>The expanded terminal.</summary>
    public double[] Expanded { get; set; } = [1200, 60, 640, 520];

    #endregion
}
