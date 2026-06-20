namespace ARK.UI.Core.Services;

public static class LocalizationService
{
    public static event EventHandler? CultureChanged;

    public static void NotifyCultureChanged() => CultureChanged?.Invoke(null, EventArgs.Empty);
}
