using System.Windows.Documents;
using System.Windows.Media;

namespace TarsClient.Controls;

/// <summary>
/// A block meter drawn as cells sized to the terminal font (VT323 is 0.4 em wide): <c>▮▮▮▮▯▯▯▯</c>. We draw it directly,
/// so it never depends on glyph coverage and stays small in the compact strip.
/// </summary>
public sealed class CellMeter : FrameworkElement
{
    #region Fields

    /// <summary>Identifies <see cref="Cells"/>.</summary>
    public static readonly DependencyProperty CellsProperty = DependencyProperty.Register(nameof(Cells), typeof(int), typeof(CellMeter),
        new FrameworkPropertyMetadata(8, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies <see cref="Level"/>.</summary>
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(CellMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies <see cref="Hot"/>.</summary>
    public static readonly DependencyProperty HotProperty = DependencyProperty.Register(nameof(Hot), typeof(bool), typeof(CellMeter),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    #endregion

    #region Properties

    /// <summary>Number of cells.</summary>
    public int Cells { get => (int)GetValue(CellsProperty); set => SetValue(CellsProperty, value); }

    /// <summary>Filled cells (0 to <see cref="Cells"/>).</summary>
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    /// <summary>Bright fill, e.g. volume above 100 %.</summary>
    public bool Hot { get => (bool)GetValue(HotProperty); set => SetValue(HotProperty, value); }

    private double CellWidth => Math.Max(3, TextElement.GetFontSize(this) * 0.4);

    #endregion

    #region Protected Methods

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(Cells * CellWidth, TextElement.GetFontSize(this) * 0.9);

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        var resources = Application.Current.Resources;
        var on = (Brush)resources[Hot ? "FgBright" : "Fg"];
        var off = new Pen((Brush)resources["FgDim"], 1);
        double width = CellWidth, height = Math.Round(TextElement.GetFontSize(this) * 0.55), y = Math.Round((ActualHeight - height) / 2);
        int filled = (int)Math.Round(Math.Clamp(Level, 0, Cells));
        for (int i = 0; i < Cells; i++)
        {
            var cell = new Rect(Math.Round(i * width), y, Math.Max(2, Math.Round(width) - 2), height);
            if (i < filled) dc.DrawRectangle(on, null, cell);
            else dc.DrawRectangle(null, off, new Rect(cell.X + 0.5, cell.Y + 0.5, cell.Width - 1, cell.Height - 1));
        }
    }

    #endregion
}
