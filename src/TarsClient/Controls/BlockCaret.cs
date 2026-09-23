using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace TarsClient.Controls;

/// <summary>
/// Replaces a TextBox's thin caret with a blinking block <c>▌</c> (530 ms), drawn by an adorner at the caret position.
/// Usage: <c>c:BlockCaret.Enabled="True"</c>. Blinks only while focused, so an idle window renders nothing.
/// </summary>
public static class BlockCaret
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(BlockCaret), new PropertyMetadata(false, OnEnabled));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);

    static void OnEnabled(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb || e.NewValue is not true) return;
        tb.CaretBrush = Brushes.Transparent;
        tb.Loaded += (_, _) =>
        {
            var layer = AdornerLayer.GetAdornerLayer(tb);
            if (layer == null || tb.Tag is CaretAdorner) return;
            var ad = new CaretAdorner(tb);
            tb.Tag = ad;
            layer.Add(ad);
        };
    }

    sealed class CaretAdorner : Adorner
    {
        readonly TextBox _tb;
        readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(530) };
        bool _on = true;

        public CaretAdorner(TextBox tb) : base(tb)
        {
            _tb = tb;
            IsHitTestVisible = false;
            _blink.Tick += (_, _) => { _on = !_on; InvalidateVisual(); };
            tb.GotKeyboardFocus += (_, _) => Restart();
            tb.LostKeyboardFocus += (_, _) => { _blink.Stop(); InvalidateVisual(); };
            tb.SelectionChanged += (_, _) => Restart();
            tb.TextChanged += (_, _) => Restart();
            tb.IsVisibleChanged += (_, _) => { if (!tb.IsVisible) _blink.Stop(); };
        }

        void Restart()
        {
            _on = true;
            if (_tb.IsKeyboardFocused) { _blink.Stop(); _blink.Start(); }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (!_tb.IsKeyboardFocused || !_on) return;
            var r = _tb.GetRectFromCharacterIndex(_tb.CaretIndex);
            if (r.IsEmpty || double.IsInfinity(r.X)) r = new Rect(0, 0, 0, _tb.ActualHeight);
            double w = Math.Max(4, _tb.FontSize * 0.28);
            var brush = (Brush)Application.Current.Resources["Fg"];
            dc.DrawRectangle(brush, null, new Rect(r.X, r.Y + 1, w, Math.Max(4, r.Height - 2)));
        }
    }
}
