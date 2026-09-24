using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TarsClient.ViewModels;

namespace TarsClient.Controls.Converters;

/// <summary>value × parameter, e.g. a banner line height from the font size.</summary>
public sealed class Scale : IValueConverter
{
    #region Public Methods

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d && double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var k) ? d * k : value;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    #endregion
}
