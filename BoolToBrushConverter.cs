using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace FT857DControl.Converters;

public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as bool?) == true ? new SolidColorBrush(Color.FromRgb(78, 207, 116)) : new SolidColorBrush(Color.FromRgb(118, 121, 124));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
