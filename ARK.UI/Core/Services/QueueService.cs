using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

public sealed class QueueService : IQueueService
{
    private readonly ILogService _logger;
    private readonly string      _storePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented                   = true,
        PropertyNameCaseInsensitive     = true,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate
    };

    public QueueStore Store { get; } = new();

    public QueueService(ILogService logger)
    {
        _logger    = logger;
        _storePath = Path.Combine(AppContext.BaseDirectory, "queues.json");
    }

    // ── Загрузка / сохранение ──────────────────────────────────────────────

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_storePath))
            {
                await _logger.LogInfoAsync(nameof(QueueService),
                    "queues.json не найден — создан пустой хранилище очередей.").ConfigureAwait(false);
                await SaveInternalAsync(ct).ConfigureAwait(false);
                return;
            }

            var json  = await File.ReadAllTextAsync(_storePath, ct).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<QueueStore>(json, JsonOpts);
            if (loaded is not null)
            {
                Store.Regions.Clear();
                foreach (var r in loaded.Regions)
                    Store.Regions.Add(r);
            }

            await _logger.LogInfoAsync(nameof(QueueService),
                $"Очереди загружены: {Store.Regions.Count} регионов.").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _logger.LogErrorAsync(nameof(QueueService),
                "Ошибка загрузки queues.json — будет использован пустой хранилище.", ex)
                .ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try   { await SaveInternalAsync(ct).ConfigureAwait(false); }
        finally { _semaphore.Release(); }
    }

    private async Task SaveInternalAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(Store, JsonOpts);
        await File.WriteAllTextAsync(_storePath, json, ct).ConfigureAwait(false);
    }

    // ── Поиск ──────────────────────────────────────────────────────────────

    public QueueRegion? GetRegionById(Guid id)
        => Store.Regions.FirstOrDefault(r => r.Id == id);

    // ── Операции с регионами ───────────────────────────────────────────────

    public bool TryAddRegion(string name, out QueueRegion? region, out string? error)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            region = null; error = "Имя региона не может быть пустым.";
            return false;
        }
        if (Store.Regions.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            region = null; error = $"Регион с именем «{name}» уже существует.";
            return false;
        }
        region = new QueueRegion { Name = name };
        Store.Regions.Add(region);
        error = null;
        return true;
    }

    public bool TryRenameRegion(QueueRegion target, string newName, out string? error)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            error = "Имя региона не может быть пустым.";
            return false;
        }
        if (Store.Regions.Any(r => r != target
                && r.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Регион с именем «{newName}» уже существует.";
            return false;
        }
        target.Name = newName;
        error = null;
        return true;
    }

    public void DeleteRegion(QueueRegion region)
        => Store.Regions.Remove(region);

    // ── Операции с папками ─────────────────────────────────────────────────

    public bool TryAddFolder(QueueRegion region, QueueFolder? parent, string name,
                             out QueueFolder? folder, out string? error)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            folder = null; error = "Имя папки не может быть пустым.";
            return false;
        }
        var collection = parent is null ? region.Folders : parent.SubFolders;
        if (collection.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            folder = null; error = $"Папка «{name}» уже существует в этом уровне.";
            return false;
        }
        folder = new QueueFolder { Name = name };
        collection.Add(folder);
        error = null;
        return true;
    }

    public bool TryRenameFolder(QueueFolder target, QueueFolder? parent,
                                QueueRegion region, string newName, out string? error)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            error = "Имя папки не может быть пустым.";
            return false;
        }
        var collection = parent is null ? region.Folders : parent.SubFolders;
        if (collection.Any(f => f != target
                && f.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Папка «{newName}» уже существует в этом уровне.";
            return false;
        }
        target.Name = newName;
        error = null;
        return true;
    }

    public void DeleteFolder(QueueRegion region, QueueFolder? parent, QueueFolder folder)
    {
        var collection = parent is null ? region.Folders : parent.SubFolders;
        collection.Remove(folder);
    }

    // ── Вспомогательный обход ──────────────────────────────────────────────

    /// <summary>Рекурсивно находит папку внутри региона и возвращает её родителя.</summary>
    public static (QueueFolder? Parent, bool Found) FindFolder(
        QueueRegion region, QueueFolder target)
    {
        foreach (var f in region.Folders)
        {
            if (f == target) return (null, true);
            var (p, found) = FindInSubFolders(f, target);
            if (found) return (p, true);
        }
        return (null, false);
    }

    private static (QueueFolder? Parent, bool Found) FindInSubFolders(
        QueueFolder parent, QueueFolder target)
    {
        foreach (var f in parent.SubFolders)
        {
            if (f == target) return (parent, true);
            var (p, found) = FindInSubFolders(f, target);
            if (found) return (p, true);
        }
        return (null, false);
    }
}
