using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TarsClient.ViewModels;

namespace TarsClient.Controls.Converters;

/// <summary>The colour of a scrollback line: TARS bright, details dim, everything else the normal orange.</summary>
public sealed class LineBrush : IValueConverter
{
    #region Public Methods

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var resources = Application.Current.Resources;
        return value switch
        {
            LineKind.Tars or LineKind.Error => resources["FgBright"],
            LineKind.Meta => resources["FgDim"],
            _ => resources["Fg"],
        };
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    #endregion
}
