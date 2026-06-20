using ARK.UI.Core.Models;

namespace ARK.UI.Core.Interfaces;

public interface IQueueService
{
    QueueStore Store { get; }

    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);

    QueueRegion? GetRegionById(Guid id);

    // ── Регионы (корневой уровень) ──────────────────────────────────────────
    bool TryAddRegion(string name, out QueueRegion? region, out string? error);
    bool TryRenameRegion(QueueRegion region, string newName, out string? error);
    void DeleteRegion(QueueRegion region);

    // ── Папки внутри региона ────────────────────────────────────────────────
    bool TryAddFolder(QueueRegion region, QueueFolder? parent, string name,
                      out QueueFolder? folder, out string? error);
    bool TryRenameFolder(QueueFolder folder, QueueFolder? parent,
                         QueueRegion region, string newName, out string? error);
    void DeleteFolder(QueueRegion region, QueueFolder? parent, QueueFolder folder);
}
