using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ARK.UI.Core.Services;

[ValueConversion(typeof(bool), typeof(Geometry))]
public sealed class PasswordEyeIconConverter : IValueConverter
{
    // IsPasswordVisible=false → пароль скрыт → иконка открытого глаза (нажмите, чтобы показать)
    private const string EyeOpenPath =
        "M12,4.5C7,4.5 2.73,7.61 1,12C2.73,16.39 7,19.5 12,19.5C17,19.5 21.27,16.39 23,12C21.27,7.61 17,4.5 12,4.5Z" +
        "M12,7C14.76,7 17,9.24 17,12C17,14.76 14.76,17 12,17C9.24,17 7,14.76 7,12C7,9.24 9.24,7 12,7Z" +
        "M12,9C13.66,9 15,10.34 15,12C15,13.66 13.66,15 12,15C10.34,15 9,13.66 9,12C9,10.34 10.34,9 12,9Z";

    // IsPasswordVisible=true → пароль открыт → иконка перечёркнутого глаза (нажмите, чтобы скрыть)
    private const string EyeSlashPath =
        "M12,7C14.76,7 17,9.24 17,12C17,12.64 16.87,13.26 16.64,13.82L19.57,16.75" +
        "C21.07,15.49 22.32,13.77 23,12C21.27,7.61 17,4.5 12,4.5C10.6,4.5 9.26,4.75 8.02,5.2L10.18,7.36" +
        "C10.74,7.13 11.35,7 12,7Z" +
        "M2,4.27L4.28,6.55L4.73,7C3.08,8.3 1.78,10.02 1,12C2.73,16.39 7,19.5 12,19.5" +
        "C13.55,19.5 14.83,19.2 16.38,18.66L16.8,19.08L19.73,22L21,20.73L3.27,3L2,4.27Z" +
        "M11.83,9L15,12.16C15,12.11 15.01,12.05 15.01,12C15.01,10.34 13.67,9 12.01,9" +
        "C11.95,9 11.9,9 11.84,9.01Z" +
        "M7.53,9.8L9.08,11.35C9.03,11.56 9,11.78 9,12C9,13.66 10.34,15 12,15" +
        "C12.22,15 12.44,14.97 12.65,14.92L14.2,16.47C13.47,16.8 12.76,17 12,17" +
        "C9.24,17 7,14.76 7,12C7,11.24 7.2,10.47 7.53,9.8Z";

    private static readonly Geometry _eyeOpen;
    private static readonly Geometry _eyeSlash;

    static PasswordEyeIconConverter()
    {
        _eyeOpen = Geometry.Parse(EyeOpenPath);
        _eyeOpen.Freeze();
        _eyeSlash = Geometry.Parse(EyeSlashPath);
        _eyeSlash.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? _eyeSlash : _eyeOpen;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
