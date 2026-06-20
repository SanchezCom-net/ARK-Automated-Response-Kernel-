using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;

namespace ARK.UI.Converters;

public sealed class HotKeyTextConverter : IMultiValueConverter
{
    public static readonly HotKeyTextConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var key  = values.Length > 0 && values[0] is Key  k ? k  : Key.None;
        var mods = values.Length > 1 && values[1] is ModifierKeys m ? m : ModifierKeys.None;

        if (key == Key.None) return "Не задана";

        var parts = new List<string>(5);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift))   parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt))     parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());

        return string.Join(" + ", parts);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
