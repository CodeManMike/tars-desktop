using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TarsClient.ViewModels;

namespace TarsClient.Controls;

public sealed class BoolToVis : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => (value is Visibility.Visible) ^ Invert;
}

/// <summary>TabIndex == parameter → Visible.</summary>
public sealed class IndexToVis : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is int i && int.TryParse(p?.ToString(), out var want) && i == want ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>TabIndex == parameter → true (for tab radio buttons, two-way).</summary>
public sealed class IndexToBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is int i && int.TryParse(p?.ToString(), out var want) && i == want;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true && int.TryParse(p?.ToString(), out var want) ? want : Binding.DoNothing;
}

public sealed class LineBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var res = Application.Current.Resources;
        return value switch
        {
            LineKind.Tars => res["FgBright"],
            LineKind.Meta => res["FgDim"],
            LineKind.User => res["Fg"],
            LineKind.Error => res["FgBright"],
            _ => res["Fg"],
        };
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>"STANDBY" → "[ STANDBY ]"</summary>
public sealed class TagFormat : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is string s && s.Length > 0 ? $"[ {s} ]" : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Flash between the palette colour and black.</summary>
public sealed class FlashBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Brushes.Black : Application.Current.Resources[p as string ?? "Fg"];
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>value × parameter (e.g. a line height from the font size).</summary>
public sealed class Scale : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is double d && double.TryParse(p?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var k) ? d * k : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
