using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TarsClient.ViewModels;

namespace TarsClient.Controls.Converters;

/// <summary>true when the bound index equals the parameter; two-way, for the tab radio buttons.</summary>
public sealed class IndexToBool : IValueConverter
{
    #region Public Methods

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int index && int.TryParse(parameter?.ToString(), out var wanted) && index == wanted;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && int.TryParse(parameter?.ToString(), out var wanted) ? wanted : Binding.DoNothing;

    #endregion
}
