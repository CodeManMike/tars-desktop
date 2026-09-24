namespace TarsClient.ViewModels;

/// <summary>One scrollback line. The text is observable so a reply can type itself out in place.</summary>
public sealed partial class TermLine(LineKind kind, string text) : ObservableObject
{
    #region Properties

    /// <summary>What the line is.</summary>
    public LineKind Kind { get; } = kind;

    /// <summary>The line's text.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = text;

    #endregion
}
