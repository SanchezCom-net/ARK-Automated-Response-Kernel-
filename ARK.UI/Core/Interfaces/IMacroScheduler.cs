using ARK.UI.Core.Models;
using ARK.UI.Core.Nodes;

namespace ARK.UI.Core.Interfaces;

public interface IMacroScheduler
{
    /// <summary>
    /// Срабатывает при смене активного профиля.
    /// Передаёт FriendlyName профиля, либо null — если активный профиль не найден.
    /// </summary>
    event EventHandler<string?>? ActiveProfileChanged;

    /// <summary>FriendlyName текущего активного профиля, либо null.</summary>
    string? ActiveProfileName { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    void Stop();

    /// <summary>
    /// Разрешает обработку пользовательских хоткеев после полной инициализации приложения.
    /// До вызова этого метода все KeyDown-события для макросов игнорируются.
    /// </summary>
    void EnableHotkeys();

    /// <summary>
    /// (Устаревший) Поставить макрос в очередь региона или запустить немедленно
    /// в зависимости от region.ExecutionMode.
    /// </summary>
    void EnqueueMacro(ProfileRegion region, MacroEntry macro, BaseNode startNode, MacroExecutionContext? initialContext = null);

    /// <summary>
    /// Новый API: поставить макрос в очередь по его RegionId (из QueueStore)
    /// или запустить немедленно, если макрос не привязан к региону.
    /// Проходит через 4-этапную проверку (эксклюзивность → регион → условия → исполнение).
    /// </summary>
    void EnqueueMacro(MacroEntry macro, BaseNode startNode, MacroExecutionContext? initialContext = null);

    /// <summary>
    /// Найти макрос по имени или GUID во всех профилях и запустить его.
    /// Возвращает false, если макрос не найден.
    /// </summary>
    Task<bool> ExecuteMacroAsync(string nameOrId, CancellationToken ct = default);

    /// <summary>
    /// Передать текстовый промпт напрямую в AI-пайплайн (Ollama → Оверлей → TTS),
    /// минуя распознавание голоса.
    /// </summary>
    Task SendNetworkPromptAsync(string text, CancellationToken ct = default);
}
