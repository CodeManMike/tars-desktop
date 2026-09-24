using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TarsClient.Controls;

/// <summary>
/// A slider drawn as text: <c>HUMOR         [███████░░░] 75%</c>. It's focusable; Left/Right (or the wheel, once
/// focused) step, Home/End jump, and a click or drag sets the value.
/// </summary>
public sealed class BlockSlider : Control
{
    #region Fields

    /// <summary>Identifies <see cref="Label"/>.</summary>
    public static readonly DependencyProperty LabelProperty = Register(nameof(Label), "");

    /// <summary>Identifies <see cref="Value"/> (two-way by default).</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(BlockSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((BlockSlider)d).Refresh()));

    /// <summary>Identifies <see cref="Minimum"/>.</summary>
    public static readonly DependencyProperty MinimumProperty = Register(nameof(Minimum), 0.0);

    /// <summary>Identifies <see cref="Maximum"/>.</summary>
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), 100.0);

    /// <summary>Identifies <see cref="Step"/>.</summary>
    public static readonly DependencyProperty StepProperty = Register(nameof(Step), 5.0);

    /// <summary>Identifies <see cref="Cells"/>.</summary>
    public static readonly DependencyProperty CellsProperty = Register(nameof(Cells), 10);

    /// <summary>Identifies <see cref="Unit"/>.</summary>
    public static readonly DependencyProperty UnitProperty = Register(nameof(Unit), "%");

    /// <summary>Identifies <see cref="LabelWidth"/>.</summary>
    public static readonly DependencyProperty LabelWidthProperty = Register(nameof(LabelWidth), 14);

    /// <summary>Identifies <see cref="Format"/>.</summary>
    public static readonly DependencyProperty FormatProperty = Register(nameof(Format), "0");

    private static readonly DependencyPropertyKey TextKey =
        DependencyProperty.RegisterReadOnly(nameof(Text), typeof(string), typeof(BlockSlider), new PropertyMetadata(""));

    /// <summary>Identifies <see cref="Text"/>.</summary>
    public static readonly DependencyProperty TextProperty = TextKey.DependencyProperty;

    #endregion

    #region Constructor

    static BlockSlider()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(BlockSlider), new FrameworkPropertyMetadata(typeof(BlockSlider)));
        FocusableProperty.OverrideMetadata(typeof(BlockSlider), new FrameworkPropertyMetadata(true));
    }

    /// <summary>Creates the slider.</summary>
    public BlockSlider() => Refresh();

    #endregion

    #region Properties

    /// <summary>The label, padded to <see cref="LabelWidth"/>.</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>The value.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>The lowest value.</summary>
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    /// <summary>The highest value.</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <summary>The step for keys, the wheel and snapping.</summary>
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    /// <summary>How many block cells the bar has.</summary>
    public int Cells { get => (int)GetValue(CellsProperty); set => SetValue(CellsProperty, value); }

    /// <summary>The unit after the number.</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    /// <summary>Characters reserved for the label.</summary>
    public int LabelWidth { get => (int)GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }

    /// <summary>The number format.</summary>
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

    /// <summary>The rendered line, bound by the template.</summary>
    public string Text => (string)GetValue(TextProperty);

    #endregion

    #region Protected Methods

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: Set(Value - Step); e.Handled = true; break;
            case Key.Right: Set(Value + Step); e.Handled = true; break;
            case Key.Home: Set(Minimum); e.Handled = true; break;
            case Key.End: Set(Maximum); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // Only a slider you've selected takes the wheel; otherwise scrolling the settings page would change values.
        if (!IsKeyboardFocused)
        {
            base.OnMouseWheel(e);
            return;
        }
        Set(Value + Math.Sign(e.Delta) * Step);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        SetFromMouse(e.GetPosition(this).X);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) SetFromMouse(e.GetPosition(this).X);
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    #endregion

    #region Private Methods

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(BlockSlider), new PropertyMetadata(fallback, (d, _) => ((BlockSlider)d).Refresh()));

    private void Refresh()
    {
        double span = Math.Max(1e-9, Maximum - Minimum);
        int filled = Math.Clamp((int)Math.Round((Value - Minimum) / span * Cells), 0, Cells);
        SetValue(TextKey, $"{Label.PadRight(LabelWidth)}[{new string('█', filled)}{new string('░', Cells - filled)}] {Value.ToString(Format)}{Unit}");
    }

    private void Set(double value)
    {
        value = Math.Round((value - Minimum) / Step) * Step + Minimum;
        Value = Math.Clamp(Math.Round(value, 4), Minimum, Maximum);
    }

    /// <summary>The label (VT323) and the blocks (Consolas fallback) differ in width, so we measure the real runs.</summary>
    private void SetFromMouse(double x)
    {
        if (Text.Length == 0 || ActualWidth <= 0) return;
        double barStart = Measure(Label.PadRight(LabelWidth) + "[");
        double barWidth = Measure(new string('█', Cells));
        Set(Minimum + Math.Clamp((x - barStart) / barWidth, 0, 1) * (Maximum - Minimum));
    }

    private double Measure(string s) =>
        new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                          new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Brushes.Black,
                          VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;

    #endregion
}
