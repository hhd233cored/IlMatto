using System.Globalization;
using System.Windows.Data;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// Converts an available width into a proportional maximum width for a chat
/// message. Keeping this in the view layer lets the message bubble remain
/// content-sized until it reaches the responsive chat width limit.
/// </summary>
public sealed class WidthRatioConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double width && !double.IsNaN(width) && !double.IsInfinity(width) &&
            double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
        {
            return Math.Max(0, width * ratio);
        }

        return 0d;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
