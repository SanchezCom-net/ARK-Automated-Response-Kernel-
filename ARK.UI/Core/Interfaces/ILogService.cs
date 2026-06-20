using ARK.UI.Core.Models;

namespace ARK.UI.Core.Interfaces;

public interface ILogService
{
    string LogDirectory { get; }

    Task LogAsync(LogLevel level, string component, string message,
        Exception? exception = null, CancellationToken cancellationToken = default);

    Task LogInfoAsync(string component, string message,
        CancellationToken cancellationToken = default) =>
        LogAsync(LogLevel.Info, component, message, null, cancellationToken);

    Task LogWarningAsync(string component, string message,
        CancellationToken cancellationToken = default) =>
        LogAsync(LogLevel.Warning, component, message, null, cancellationToken);

    Task LogErrorAsync(string component, string message,
        Exception? exception = null, CancellationToken cancellationToken = default) =>
        LogAsync(LogLevel.Error, component, message, exception, cancellationToken);

    // ── Экспоненциальное подавление повторяющихся логов ───────────────────────
    // categoryKey — уникальный ключ типа события (напр. "WS_CONN_ERROR").
    // Интервал между записями: 1→2→4→...→300 с (удваивается при каждом фактическом логе).
    // Горячий путь (suppress): ValueTask.CompletedTask, zero-allocation.
    ValueTask LogSuppressedAsync(string categoryKey, LogLevel level, string component,
        string message, Exception? exception = null);

    // Сбрасывает интервал подавления для ключа (вызывать при восстановлении соединения и т.п.).
    void ResetLogSuppression(string categoryKey);

    // Удобный враппер для уровня Error (не требует импорта LogLevel в вызывающем коде).
    ValueTask LogErrorSuppressedAsync(string categoryKey, string component,
        string message, Exception? exception = null) =>
        LogSuppressedAsync(categoryKey, LogLevel.Error, component, message, exception);
}
