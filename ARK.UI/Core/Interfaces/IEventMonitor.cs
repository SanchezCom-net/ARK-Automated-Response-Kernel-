namespace ARK.UI.Core.Interfaces;

/// <summary>
/// Кэширующий слухач триггеров. Сканирует Release-макросы при старте,
/// подписывается на IInputService.KeyDown и ISpeechTriggerService.SpeechRecognized
/// и маршрутизирует совпадения в IMacroOrchestrator.
/// </summary>
public interface IEventMonitor
{
    /// <summary>
    /// Загрузить/перестроить кэш триггеров из всех Release-макросов.
    /// Вызывается на старте приложения и при изменении system_map.json.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Перестроить кэш без повторной подписки на события.</summary>
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>
    /// Принудительно перестроить кэш триггеров из Release-макросов.
    /// Вызывается IStorageManager.MacroIndexChanged — не требует повторной подписки на системные события.
    /// </summary>
    Task RefreshTriggersCacheAsync(CancellationToken ct = default);
}
