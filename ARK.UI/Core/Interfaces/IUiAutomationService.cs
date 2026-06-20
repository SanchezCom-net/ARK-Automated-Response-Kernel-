using ARK.UI.Core.Models;

namespace ARK.UI.Core.Interfaces;

public interface IUiAutomationService
{
    /// <summary>Хэндл активного (foreground) окна через Win32 GetForegroundWindow.</summary>
    nint GetActiveWindowHandle();

    /// <summary>
    /// Сканирует активное окно через Windows UI Automation и возвращает видимые
    /// интерактивные элементы (Button / Edit / MenuItem / TabItem) с экранными
    /// координатами центров. При сбое UIA возвращает пустой список — не бросает.
    /// </summary>
    Task<List<UiElementInfo>> GetClickableElementsAsync(CancellationToken cancellationToken = default);
}
