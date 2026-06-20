namespace ARK.UI.Core.Interfaces;

/// <summary>Аргументы события запуска/завершения процесса ОС.</summary>
public sealed record ProcessWatcherEventArgs(string ProcessName, int ProcessId);

/// <summary>
/// Следит за запуском и завершением процессов ОС методом периодического опроса.
/// Предоставляет актуальный кэш имён запущенных процессов для мгновенного доступа
/// из MacroScheduler без блокирующего System.Diagnostics.Process.GetProcesses().
/// </summary>
public interface IProcessWatcher
{
    /// <summary>Снимок имён процессов, работающих прямо сейчас (обновляется каждые 2 сек).</summary>
    IReadOnlySet<string> RunningProcessNames { get; }

    /// <summary>Процесс запущен (diff между опросами).</summary>
    event EventHandler<ProcessWatcherEventArgs>? ProcessStarted;

    /// <summary>Процесс завершён (diff между опросами).</summary>
    event EventHandler<ProcessWatcherEventArgs>? ProcessExited;

    /// <summary>Запускает фоновый опрос. Безопасно вызывать повторно — повторный старт игнорируется.</summary>
    void Start(CancellationToken ct = default);

    /// <summary>Останавливает фоновый опрос.</summary>
    void Stop();
}
