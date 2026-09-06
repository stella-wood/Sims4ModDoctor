using System.Globalization;
using System.Windows.Data;

namespace Sims4ModDoctor.Desktop.Infrastructure;

public sealed class MultiplyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double number
            || !double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var factor))
        {
            return value;
        }

        return number * factor;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
