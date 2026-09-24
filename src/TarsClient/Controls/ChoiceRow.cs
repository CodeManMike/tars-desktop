using System.Collections;
using System.Windows.Controls;
using System.Windows.Input;

namespace TarsClient.Controls;

/// <summary>A cycling choice: <c>INPUT         &lt; Headset Microphone &gt;</c>. Left/Right/Enter or a click cycles.</summary>
public sealed class ChoiceRow : Control
{
    #region Fields

    /// <summary>Identifies <see cref="Label"/>.</summary>
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(ChoiceRow), new PropertyMetadata("", Changed));

    /// <summary>Identifies <see cref="Items"/>.</summary>
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(IList), typeof(ChoiceRow), new PropertyMetadata(null, Changed));

    /// <summary>Identifies <see cref="SelectedIndex"/> (two-way by default).</summary>
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(ChoiceRow),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));

    /// <summary>Identifies <see cref="LabelWidth"/>.</summary>
    public static readonly DependencyProperty LabelWidthProperty =
        DependencyProperty.Register(nameof(LabelWidth), typeof(int), typeof(ChoiceRow), new PropertyMetadata(14, Changed));

    private static readonly DependencyPropertyKey TextKey =
        DependencyProperty.RegisterReadOnly(nameof(Text), typeof(string), typeof(ChoiceRow), new PropertyMetadata(""));

    /// <summary>Identifies <see cref="Text"/>.</summary>
    public static readonly DependencyProperty TextProperty = TextKey.DependencyProperty;

    #endregion

    #region Constructor

    static ChoiceRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ChoiceRow), new FrameworkPropertyMetadata(typeof(ChoiceRow)));
        FocusableProperty.OverrideMetadata(typeof(ChoiceRow), new FrameworkPropertyMetadata(true));
    }

    #endregion

    #region Properties

    /// <summary>The label, padded to <see cref="LabelWidth"/>.</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>The choices; each shows its <c>ToString()</c>.</summary>
    public IList? Items { get => (IList?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    /// <summary>The selected choice.</summary>
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    /// <summary>Characters reserved for the label.</summary>
    public int LabelWidth { get => (int)GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }

    /// <summary>The rendered line, bound by the template.</summary>
    public string Text => (string)GetValue(TextProperty);

    #endregion

    #region Protected Methods

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: Cycle(-1); e.Handled = true; break;
            case Key.Right or Key.Enter or Key.Space: Cycle(1); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        Cycle(1);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        Cycle(-1);
        e.Handled = true;
    }

    #endregion

    #region Private Methods

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ChoiceRow)d).Refresh();

    private void Refresh()
    {
        var current = Items is { Count: > 0 } items ? items[Math.Clamp(SelectedIndex, 0, items.Count - 1)]?.ToString() : "-";
        SetValue(TextKey, $"{Label.PadRight(LabelWidth)}< {current} >");
    }

    private void Cycle(int direction)
    {
        if (Items is not { Count: > 0 } items) return;
        SelectedIndex = (SelectedIndex + direction + items.Count) % items.Count;
    }

    #endregion
}
