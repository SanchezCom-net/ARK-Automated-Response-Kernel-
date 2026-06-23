using ARK.UI.Core.Models;

namespace ARK.UI.Core.Interfaces;

public interface IQueueManager
{
    Task<RegionQueue?> GetRegionAsync(Guid regionId, CancellationToken ct = default);
    Task<IReadOnlyList<RegionQueue>> GetAllRegionsAsync(CancellationToken ct = default);

    Task SaveRegionAsync(RegionQueue region, CancellationToken ct = default);
    Task DeleteRegionAsync(Guid regionId, CancellationToken ct = default);

    Task AddMacroToRegionAsync(Guid regionId, Guid macroId, int priority, CancellationToken ct = default);
    Task RemoveMacroFromRegionAsync(Guid regionId, Guid macroId, CancellationToken ct = default);
    Task UpdatePriorityAsync(Guid regionId, Guid macroId, int priority, CancellationToken ct = default);

    /// <summary>
    /// Runtime-постановка в очередь: добавляет запись в файл региона
    /// и запускает фоновую обработку очереди (<see cref="TryProcessRegionAsync"/>).
    /// В отличие от <see cref="AddMacroToRegionAsync"/> допускает дубликаты —
    /// каждый вызов означает «запустить макрос ещё раз».
    /// <paramref name="triggerNodeId"/> — ID ноды-триггера, с которой начать выполнение.
    /// Guid.Empty = устаревший путь (старт с TriggerRootNode).
    /// </summary>
    Task EnqueueAsync(Guid macroId, Guid triggerNodeId, Guid regionId, int priority, CancellationToken ct = default);
}
