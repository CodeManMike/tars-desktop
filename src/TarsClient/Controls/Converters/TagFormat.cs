using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TarsClient.ViewModels;

namespace TarsClient.Controls.Converters;

/// <summary>"STANDBY" → "[ STANDBY ]" for the title bar.</summary>
public sealed class TagFormat : IValueConverter
{
    #region Public Methods

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string tag && tag.Length > 0 ? $"[ {tag} ]" : "";

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    #endregion
}
