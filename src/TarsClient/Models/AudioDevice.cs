namespace TarsClient.Models;

/// <summary>An audio endpoint as the settings screen shows it.</summary>
/// <param name="Id">The endpoint id; empty means the Windows default.</param>
/// <param name="Name">The friendly name.</param>
public sealed record AudioDevice(string Id, string Name)
{
    #region Public Methods

    /// <summary>The friendly name, which is what a <c>ChoiceRow</c> displays.</summary>
    public override string ToString() => Name;

    #endregion
}
