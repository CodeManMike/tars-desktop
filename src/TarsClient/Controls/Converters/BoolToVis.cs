using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TarsClient.ViewModels;

namespace TarsClient.Controls.Converters;

/// <summary>true → Visible, false → Collapsed (reversed with <see cref="Invert"/>).</summary>
public sealed class BoolToVis : IValueConverter
{
    #region Properties

    /// <summary>Show on false instead of true.</summary>
    public bool Invert { get; set; }

    #endregion

    #region Public Methods

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;

    #endregion
}
