using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TarsClient.Controls;

/// <summary>
/// Replaces a TextBox's thin caret with a blinking block <c>▌</c> (530 ms), drawn by an adorner at the caret position.
/// Usage: <c>c:BlockCaret.Enabled="True"</c>. It blinks only while focused, so an idle window renders nothing.
/// </summary>
public static class BlockCaret
{
    #region Fields

    /// <summary>Identifies the <c>Enabled</c> attached property.</summary>
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(BlockCaret), new PropertyMetadata(false, OnEnabled));

    #endregion

    #region Public Methods

    /// <summary>Gets whether the block caret is on.</summary>
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    /// <summary>Turns the block caret on or off.</summary>
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    #endregion

    #region Private Methods

    private static void OnEnabled(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || e.NewValue is not true) return;
        box.CaretBrush = Brushes.Transparent;
        box.Loaded += (_, _) =>
        {
            var layer = AdornerLayer.GetAdornerLayer(box);
            if (layer == null || box.Tag is CaretAdorner) return;
            var adorner = new CaretAdorner(box);
            box.Tag = adorner;
            layer.Add(adorner);
        };
    }

    #endregion

    #region Nested Types

    private sealed class CaretAdorner : Adorner
    {
        private readonly TextBox _box;
        private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(530) };
        private bool _on = true;

        public CaretAdorner(TextBox box) : base(box)
        {
            _box = box;
            IsHitTestVisible = false;
            _blink.Tick += (_, _) =>
            {
                _on = !_on;
                InvalidateVisual();
            };
            box.GotKeyboardFocus += (_, _) => Restart();
            box.LostKeyboardFocus += (_, _) =>
            {
                _blink.Stop();
                InvalidateVisual();
            };
            box.SelectionChanged += (_, _) => Restart();
            box.TextChanged += (_, _) => Restart();
            box.IsVisibleChanged += (_, _) =>
            {
                if (!box.IsVisible) _blink.Stop();
            };
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (!_box.IsKeyboardFocused || !_on) return;
            var caret = _box.GetRectFromCharacterIndex(_box.CaretIndex);
            if (caret.IsEmpty || double.IsInfinity(caret.X)) caret = new Rect(0, 0, 0, _box.ActualHeight);
            double width = Math.Max(4, _box.FontSize * 0.28);
            var brush = (Brush)Application.Current.Resources["Fg"];
            dc.DrawRectangle(brush, null, new Rect(caret.X, caret.Y + 1, width, Math.Max(4, caret.Height - 2)));
        }

        private void Restart()
        {
            _on = true;
            if (_box.IsKeyboardFocused)
            {
                _blink.Stop();
                _blink.Start();
            }
            InvalidateVisual();
        }
    }

    #endregion
}
