using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace TarsClient.Controls;

/// <summary>
/// A slider drawn as text: <c>HUMOR       [███████░░░] 75%</c>.
/// Focusable; Left/Right (or the wheel, once focused) step, Home/End jump, click sets.
/// </summary>
public sealed class BlockSlider : Control
{
    static BlockSlider()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(BlockSlider), new FrameworkPropertyMetadata(typeof(BlockSlider)));
        FocusableProperty.OverrideMetadata(typeof(BlockSlider), new FrameworkPropertyMetadata(true));
    }

    public static readonly DependencyProperty LabelProperty = Reg(nameof(Label), "");
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(BlockSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((BlockSlider)d).Refresh()));
    public static readonly DependencyProperty MinimumProperty = Reg(nameof(Minimum), 0.0);
    public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), 100.0);
    public static readonly DependencyProperty StepProperty = Reg(nameof(Step), 5.0);
    public static readonly DependencyProperty CellsProperty = Reg(nameof(Cells), 10);
    public static readonly DependencyProperty UnitProperty = Reg(nameof(Unit), "%");
    public static readonly DependencyProperty LabelWidthProperty = Reg(nameof(LabelWidth), 14);
    public static readonly DependencyProperty FormatProperty = Reg(nameof(Format), "0");
    static readonly DependencyPropertyKey TextKey = DependencyProperty.RegisterReadOnly(nameof(Text), typeof(string), typeof(BlockSlider), new PropertyMetadata(""));
    public static readonly DependencyProperty TextProperty = TextKey.DependencyProperty;

    static DependencyProperty Reg<T>(string name, T def) =>
        DependencyProperty.Register(name, typeof(T), typeof(BlockSlider), new PropertyMetadata(def, (d, _) => ((BlockSlider)d).Refresh()));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public int Cells { get => (int)GetValue(CellsProperty); set => SetValue(CellsProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public int LabelWidth { get => (int)GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public string Text => (string)GetValue(TextProperty);

    public BlockSlider() => Refresh();

    void Refresh()
    {
        double span = Math.Max(1e-9, Maximum - Minimum);
        int filled = (int)Math.Round((Value - Minimum) / span * Cells);
        filled = Math.Clamp(filled, 0, Cells);
        SetValue(TextKey, $"{Label.PadRight(LabelWidth)}[{new string('█', filled)}{new string('░', Cells - filled)}] {Value.ToString(Format)}{Unit}");
    }

    void Set(double v)
    {
        v = Math.Round((v - Minimum) / Step) * Step + Minimum;
        Value = Math.Clamp(Math.Round(v, 4), Minimum, Maximum);
    }

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

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // Only a slider you've selected takes the wheel; otherwise scrolling the settings page would change values.
        if (!IsKeyboardFocused) { base.OnMouseWheel(e); return; }
        Set(Value + Math.Sign(e.Delta) * Step);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        SetFromMouse(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) SetFromMouse(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    void SetFromMouse(double x)
    {
        // Label (VT323) and blocks (Consolas fallback) differ in width: measure the real runs.
        if (Text.Length == 0 || ActualWidth <= 0) return;
        double barStart = Measure(Label.PadRight(LabelWidth) + "[");
        double barWidth = Measure(new string('█', Cells));
        Set(Minimum + Math.Clamp((x - barStart) / barWidth, 0, 1) * (Maximum - Minimum));
    }

    double Measure(string s)
    {
        var ft = new System.Windows.Media.FormattedText(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, System.Windows.Media.Brushes.Black,
            System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace;
    }
}

/// <summary>
/// A cycling choice: <c>INPUT DEVICE  &lt; Headset Microphone &gt;</c>. Left/Right/Enter/click cycles.
/// </summary>
public sealed class ChoiceRow : Control
{
    static ChoiceRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ChoiceRow), new FrameworkPropertyMetadata(typeof(ChoiceRow)));
        FocusableProperty.OverrideMetadata(typeof(ChoiceRow), new FrameworkPropertyMetadata(true));
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(ChoiceRow), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IList), typeof(ChoiceRow), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(ChoiceRow),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(nameof(LabelWidth), typeof(int), typeof(ChoiceRow), new PropertyMetadata(14, Changed));
    static readonly DependencyPropertyKey TextKey = DependencyProperty.RegisterReadOnly(nameof(Text), typeof(string), typeof(ChoiceRow), new PropertyMetadata(""));
    public static readonly DependencyProperty TextProperty = TextKey.DependencyProperty;

    static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ChoiceRow)d).Refresh();

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public IList? Items { get => (IList?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public int LabelWidth { get => (int)GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }
    public string Text => (string)GetValue(TextProperty);

    void Refresh()
    {
        var items = Items;
        var cur = items is { Count: > 0 } ? items[Math.Clamp(SelectedIndex, 0, items.Count - 1)]?.ToString() : "-";
        SetValue(TextKey, $"{Label.PadRight(LabelWidth)}< {cur} >");
    }

    void Cycle(int dir)
    {
        if (Items is not { Count: > 0 } items) return;
        SelectedIndex = (SelectedIndex + dir + items.Count) % items.Count;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: Cycle(-1); e.Handled = true; break;
            case Key.Right or Key.Enter or Key.Space: Cycle(1); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { Focus(); Cycle(1); e.Handled = true; }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) { Focus(); Cycle(-1); e.Handled = true; }
}

/// <summary>
/// A block meter drawn as cells sized to the terminal font (VT323 is 0.4 em wide): <c>▮▮▮▮▯▯▯▯</c>.
/// Drawn directly, so it never depends on glyph coverage and stays small in the compact strip.
/// </summary>
public sealed class CellMeter : FrameworkElement
{
    public static readonly DependencyProperty CellsProperty = DependencyProperty.Register(nameof(Cells), typeof(int), typeof(CellMeter),
        new FrameworkPropertyMetadata(8, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(CellMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HotProperty = DependencyProperty.Register(nameof(Hot), typeof(bool), typeof(CellMeter),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Number of cells.</summary>
    public int Cells { get => (int)GetValue(CellsProperty); set => SetValue(CellsProperty, value); }
    /// <summary>Filled cells (0..Cells).</summary>
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    /// <summary>Bright fill (e.g. volume above 100 %).</summary>
    public bool Hot { get => (bool)GetValue(HotProperty); set => SetValue(HotProperty, value); }

    double CellW => Math.Max(3, TextElement.GetFontSize(this) * 0.4);

    protected override Size MeasureOverride(Size available) => new(Cells * CellW, TextElement.GetFontSize(this) * 0.9);

    protected override void OnRender(System.Windows.Media.DrawingContext dc)
    {
        var res = Application.Current.Resources;
        var on = (System.Windows.Media.Brush)res[Hot ? "FgBright" : "Fg"];
        var off = (System.Windows.Media.Brush)res["FgDim"];
        double fs = TextElement.GetFontSize(this), w = CellW, h = Math.Round(fs * 0.55), y = Math.Round((ActualHeight - h) / 2);
        int filled = (int)Math.Round(Math.Clamp(Level, 0, Cells));
        for (int i = 0; i < Cells; i++)
        {
            var r = new Rect(Math.Round(i * w), y, Math.Max(2, Math.Round(w) - 2), h);
            if (i < filled) dc.DrawRectangle(on, null, r);
            else dc.DrawRectangle(null, new System.Windows.Media.Pen(off, 1), new Rect(r.X + 0.5, r.Y + 0.5, r.Width - 1, r.Height - 1));
        }
    }
}
