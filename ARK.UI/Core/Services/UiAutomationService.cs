using System.Runtime.InteropServices;
using System.Windows.Automation;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

/// <summary>
/// Сканер интерфейса активного окна через нативную Windows UI Automation (UIA).
/// Даёт точные экранные координаты элементов без привязки к жёстким координатам скриншота.
/// </summary>
public sealed partial class UiAutomationService : IUiAutomationService
{
    private const string Component = "UiAutomationService";

    // Потолок выдачи: защищает промпт Ollama от раздувания на сложных окнах (браузеры и т.п.)
    private const int MaxElements = 60;

    private readonly ILogService _logger;

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    public UiAutomationService(ILogService logger) => _logger = logger;

    public nint GetActiveWindowHandle() => GetForegroundWindow();

    public async Task<List<UiElementInfo>> GetClickableElementsAsync(CancellationToken cancellationToken = default)
    {
        var hwnd = GetActiveWindowHandle();
        if (hwnd == nint.Zero) return [];

        // UIA-клиент предпочитает MTA: FindAll(Descendants) на сложных окнах занимает
        // сотни миллисекунд — уводим сканирование в Thread Pool, не трогая Dispatcher
        return await Task.Run(() => ScanWindow(hwnd, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private List<UiElementInfo> ScanWindow(nint hwnd, CancellationToken ct)
    {
        var result = new List<UiElementInfo>();
        try
        {
            var root = AutomationElement.FromHandle(hwnd);

            // Видимые интерактивные элементы: Button / Edit / MenuItem / TabItem
            var condition = new AndCondition(
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false));

            var found = root.FindAll(TreeScope.Descendants, condition);

            foreach (AutomationElement element in found)
            {
                if (ct.IsCancellationRequested || result.Count >= MaxElements) break;

                try
                {
                    var current = element.Current;
                    var rect    = current.BoundingRectangle;
                    if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;

                    result.Add(new UiElementInfo
                    {
                        Name         = current.Name ?? string.Empty,
                        AutomationId = current.AutomationId ?? string.Empty,
                        // ProgrammaticName вида "ControlType.Button" → "Button"
                        ControlType  = current.ControlType.ProgrammaticName
                            .Replace("ControlType.", string.Empty),
                        CenterX      = rect.X + rect.Width  / 2,
                        CenterY      = rect.Y + rect.Height / 2
                    });
                }
                catch (ElementNotAvailableException)
                {
                    // Элемент исчез между FindAll и чтением свойств — пропускаем
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Окно закрылось/недоступно для UIA (elevated-процессы и т.п.) — деградируем тихо
            _ = _logger.LogWarningAsync(Component,
                $"Сканирование UIA активного окна не удалось: {ex.Message}");
        }
        return result;
    }
}
