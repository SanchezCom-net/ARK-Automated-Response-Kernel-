using System.Globalization;
using System.Windows.Data;
using ARK.UI.Resources;

namespace ARK.UI.Converters;

[ValueConversion(typeof(Enum), typeof(string))]
public sealed class EnumToLocalizedDescriptionConverter : IValueConverter
{
    public static readonly EnumToLocalizedDescriptionConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;
        var key = $"Enum_{value.GetType().Name}_{value}";
        return Strings.ResourceManager.GetString(key, Strings.Culture) ?? value.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
