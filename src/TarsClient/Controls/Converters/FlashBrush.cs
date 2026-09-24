using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TarsClient.ViewModels;

namespace TarsClient.Controls.Converters;

/// <summary>Alarm flash: black while the flash is on, otherwise the palette brush named by the parameter.</summary>
public sealed class FlashBrush : IValueConverter
{
    #region Public Methods

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Brushes.Black : Application.Current.Resources[parameter as string ?? "Fg"];

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    #endregion
}
