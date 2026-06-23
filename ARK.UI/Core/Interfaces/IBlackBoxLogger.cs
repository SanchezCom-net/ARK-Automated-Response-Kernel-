namespace ARK.UI.Core.Interfaces;

public interface IBlackBoxLogger
{
    // Неблокирующая запись — TryWrite в Channel<T>. Никогда не ждёт диска.
    void Log(Guid nodeId, string message, Exception? ex = null);

    // Сигнал на чистое завершение фонового потребителя канала.
    Task FlushAsync(CancellationToken ct = default);
}
