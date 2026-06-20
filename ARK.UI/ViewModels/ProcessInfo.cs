using System.Windows.Media.Imaging;

namespace ARK.UI.ViewModels;

/// <summary>Информация о запущенном процессе: имя, путь к exe и его иконка.</summary>
public sealed record ProcessInfo(string ProcessName, string ProcessPath, BitmapSource? Icon)
{
    public override string ToString() => ProcessName;
}
