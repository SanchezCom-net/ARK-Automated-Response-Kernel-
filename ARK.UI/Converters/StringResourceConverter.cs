using System.Globalization;
using System.Windows.Data;
using ARK.UI.Resources;

namespace ARK.UI.Converters;

public sealed class StringResourceConverter : IValueConverter
{
    public static readonly StringResourceConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string key)
            return Strings.ResourceManager.GetString(key, culture) ?? key;
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
