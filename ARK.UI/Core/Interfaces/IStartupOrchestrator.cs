namespace ARK.UI.Core.Interfaces;

/// <summary>Результат выполнения одной фазы warm-up последовательности.</summary>
public sealed record StartupPhaseEventArgs(string PhaseName, bool Success, string? ErrorMessage = null);

/// <summary>
/// Оркестрирует последовательный прогрев подсистем ARK после старта приложения.
/// Запускается в фоновом потоке (Task.Run из App.xaml.cs).
/// Публикует IsReady=true и стреляет ReadyStateChanged когда все фазы завершены.
/// </summary>
public interface IStartupOrchestrator
{
    /// <summary>Все подсистемы инициализированы.</summary>
    bool IsReady { get; }

    /// <summary>Переход IsReady: false → true.</summary>
    event EventHandler? ReadyStateChanged;

    /// <summary>Фаза warm-up завершена (успешно или с ошибкой).</summary>
    event EventHandler<StartupPhaseEventArgs>? PhaseCompleted;

    /// <summary>
    /// Запускает warm-up последовательность. Вызывается один раз из App через Task.Run.
    /// Внутри: GPU → Speech → MacroIndex → Processes → IsReady.
    /// </summary>
    Task RunAsync(CancellationToken ct = default);
}
