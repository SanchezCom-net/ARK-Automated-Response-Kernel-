namespace ARK.UI.Core.Nodes;

/// <summary>
/// Определяет поведение макроса при получении нового триггера, пока предыдущее
/// выполнение ещё не завершено. Хранится в TriggerRootNode.
/// </summary>
public enum SelfCollisionStrategy
{
    /// <summary>Запускать параллельно. Каждый триггер = отдельный экземпляр.</summary>
    Parallel,

    /// <summary>Игнорировать новый триггер, пока макрос выполняется.</summary>
    Drop,

    /// <summary>Поставить новый вызов в приватную очередь макроса (1 слот ожидания).</summary>
    Queue,

    /// <summary>Отменить текущее выполнение и немедленно запустить макрос заново.</summary>
    Restart,
}
