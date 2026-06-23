using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Services;

public sealed class QueueManager : IQueueManager
{
    private const string Component = nameof(QueueManager);

    private readonly ILogService              _logger;
    private readonly IStorageManager          _storageManager;
    private readonly IServiceProvider         _serviceProvider;
    private readonly IActiveDocumentRegistry  _activeDocRegistry;

    // Файловый мьютекс: защищает чтение/запись JSON-файлов очередей
    private readonly SemaphoreSlim _queueLock = new(1, 1);

    // Исполнительные мьютексы: один Semaphore(1,1) на регион — StrictQueue
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _regionLocks = new();

    private readonly string _queuesDir;
    private readonly string _backupDir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented                   = true,
        PropertyNameCaseInsensitive     = true,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate
    };

    public QueueManager(ILogService logger, IStorageManager storageManager,
        IServiceProvider serviceProvider, IActiveDocumentRegistry activeDocRegistry)
    {
        _logger            = logger;
        _storageManager    = storageManager;
        _serviceProvider   = serviceProvider;
        _activeDocRegistry = activeDocRegistry;

        var baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        _queuesDir  = Path.Combine(baseDir, "queues");
        _backupDir  = Path.Combine(baseDir, "queues", "backup");
    }

    // ── Runtime-постановка в очередь (исполнение) ────────────────────────────

    public async Task EnqueueAsync(
        Guid macroId, Guid triggerNodeId, Guid regionId, int priority, CancellationToken ct = default)
    {
        // Добавляем в файл региона (дубликаты разрешены — каждый вызов = одно выполнение)
        await _queueLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var region = await LoadRegionInternalAsync(regionId, ct).ConfigureAwait(false)
                         ?? new RegionQueue { RegionId = regionId };

            region.Entries.Add(new RegionQueueEntry
            {
                MacroId       = macroId,
                TriggerNodeId = triggerNodeId,
                Priority      = priority
            });
            await SaveRegionInternalAsync(region, ct).ConfigureAwait(false);
        }
        finally { _queueLock.Release(); }

        // Fire-and-forget: запускаем обработку очереди, не блокируя вызывающий код
        _ = TryProcessRegionAsync(regionId);
    }

    // ── Обработчик очереди региона (StrictQueue) ─────────────────────────────

    private async Task TryProcessRegionAsync(Guid regionId)
    {
        var sem = _regionLocks.GetOrAdd(regionId, _ => new SemaphoreSlim(1, 1));

        // Не-блокирующий захват: если регион уже обрабатывается — выходим.
        // Текущий цикл while сам подберёт добавленную запись на следующей итерации.
        if (!await sem.WaitAsync(0).ConfigureAwait(false)) return;

        try
        {
            while (true)
            {
                var entry = await DequeueHighestPriorityAsync(regionId).ConfigureAwait(false);
                if (entry is null) break;

                try
                {
                    await ExecuteMacroByIdAsync(entry).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await _logger.LogErrorAsync(Component,
                        $"Ошибка выполнения макроса {entry.MacroId} в регионе {regionId}.", ex)
                        .ConfigureAwait(false);
                }
            }
        }
        finally { sem.Release(); }
    }

    // Атомарно читает запись с наивысшим приоритетом и удаляет её из файла
    private async Task<RegionQueueEntry?> DequeueHighestPriorityAsync(Guid regionId)
    {
        await _queueLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var region = await LoadRegionInternalAsync(regionId, default).ConfigureAwait(false);
            if (region is null || region.Entries.Count == 0) return null;

            // Priority == 0 → «обычный» (самый низкий); 1–999 → строгий, 1 = первым
            var entry = region.Entries
                .OrderBy(e => e.Priority == 0 ? int.MaxValue : e.Priority)
                .First();

            region.Entries.Remove(entry);
            await SaveRegionInternalAsync(region, default).ConfigureAwait(false);
            return entry;
        }
        finally { _queueLock.Release(); }
    }

    // Загружает макрос, регистрирует ноды, запускает через Transient NodeEngine
    private async Task ExecuteMacroByIdAsync(RegionQueueEntry entry)
    {
        MacroDocument doc;
        try
        {
            doc = await _storageManager.LoadMacroAsync(entry.MacroId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                $"Не удалось загрузить макрос {entry.MacroId} для выполнения.", ex).ConfigureAwait(false);
            return;
        }

        // Transient — каждое выполнение получает изолированный экземпляр движка.
        // Если документ открыт в редакторе — используем отображаемые ноды (→ State-анимации).
        var active = _activeDocRegistry.GetActive(doc.Id);
        var engine = _serviceProvider.GetRequiredService<INodeEngine>();
        engine.RegisterNodes(
            active?.Nodes ?? doc.Macro.VisualNodes.Select(vn => vn.LogicalNode).ToList());
        engine.RegisterConnections(
            active?.Connections ?? doc.Macro.VisualConnections);

        if (entry.TriggerNodeId != Guid.Empty)
        {
            // V3: изолированный запуск с конкретной ноды-триггера
            // IsInteractiveTest в контексте устанавливает новый StartAsync перегруз
            await engine.StartAsync(entry.TriggerNodeId, initPacket: null).ConfigureAwait(false);
        }
        else
        {
            // Устаревший путь: старт с StartNodeId (TriggerRootNode)
            if (doc.Macro.StartNodeId is not { } startNodeId)
            {
                await _logger.LogErrorAsync(Component,
                    $"Макрос '{doc.UserDefinedName}' ({entry.MacroId}) не имеет стартовой ноды.")
                    .ConfigureAwait(false);
                return;
            }

            var context = new MacroExecutionContext();
            context.Variables["IsInteractiveTest"] = true;
            await engine.StartAsync(startNodeId, context).ConfigureAwait(false);
        }
    }

    public async Task<RegionQueue?> GetRegionAsync(Guid regionId, CancellationToken ct = default)
    {
        await _queueLock.WaitAsync(ct).ConfigureAwait(false);
        try   { return await LoadRegionInternalAsync(regionId, ct).ConfigureAwait(false); }
        finally { _queueLock.Release(); }
    }

    public async Task<IReadOnlyList<RegionQueue>> GetAllRegionsAsync(CancellationToken ct = default)
    {
        await _queueLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new List<RegionQueue>();
            if (!Directory.Exists(_queuesDir)) return result.AsReadOnly();

            foreach (var file in Directory.GetFiles(_queuesDir, "*.json"))
            {
                try
                {
                    var json   = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                    var region = JsonSerializer.Deserialize<RegionQueue>(json, JsonOpts);
                    if (region is not null) result.Add(region);
                }
                catch (Exception ex)
                {
                    await _logger.LogErrorAsync(nameof(QueueManager),
                        $"Ошибка чтения файла очереди: {Path.GetFileName(file)}", ex).ConfigureAwait(false);
                }
            }
            return result.AsReadOnly();
        }
        finally { _queueLock.Release(); }
    }

    public async Task SaveRegionAsync(RegionQueue region, CancellationToken ct = default)
    {
        await _queueLock.WaitAsync(ct).ConfigureAwait(false);
        try   { await SaveRegionInternalAsync(region, ct).ConfigureAwait(false); }
        finally { _queueLock.Release(); }
    }

    public async Task DeleteRegionAsync(Guid regionId, CancellationToken ct = default)
    {
        await _queueLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = RegionPath(regionId);
            if (File.Exists(path)) File.Delete(path);
        }
        finally { _queueLock.Release(); }
    }

    public async Task AddMacroToRegionAsync(
        Guid regionId, Guid macroId, int priority, CancellationToken ct = default)
    {
        await _queueLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var region = await LoadRegionInternalAsync(regionId, ct).ConfigureAwait(false)
                         ?? new RegionQueue { RegionId = regionId };

            if (!region.Entries.Any(e => e.MacroId == macroId))
                region.Entries.Add(new RegionQueueEntry { MacroId = macroId, Priority = priority });

            await SaveRegionInternalAsync(region, ct).ConfigureAwait(false);
        }
        finally { _queueLock.Release(); }
    }

    public async Task RemoveMacroFromRegionAsync(
        Guid regionId, Guid macroId, CancellationToken ct = default)
    {
        await _queueLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var region = await LoadRegionInternalAsync(regionId, ct).ConfigureAwait(false);
            if (region is null) return;

            var entry = region.Entries.FirstOrDefault(e => e.MacroId == macroId);
            if (entry is null) return;

            region.Entries.Remove(entry);
            await SaveRegionInternalAsync(region, ct).ConfigureAwait(false);
        }
        finally { _queueLock.Release(); }
    }

    public async Task UpdatePriorityAsync(
        Guid regionId, Guid macroId, int priority, CancellationToken ct = default)
    {
        await _queueLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var region = await LoadRegionInternalAsync(regionId, ct).ConfigureAwait(false);
            if (region is null) return;

            var entry = region.Entries.FirstOrDefault(e => e.MacroId == macroId);
            if (entry is null) return;

            entry.Priority = priority;
            await SaveRegionInternalAsync(region, ct).ConfigureAwait(false);
        }
        finally { _queueLock.Release(); }
    }

    // ── Внутренние методы (только под _queueLock) ────────────────────────────

    private async Task<RegionQueue?> LoadRegionInternalAsync(Guid regionId, CancellationToken ct)
    {
        var path = RegionPath(regionId);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<RegionQueue>(json, JsonOpts);
    }

    private async Task SaveRegionInternalAsync(RegionQueue region, CancellationToken ct)
    {
        Directory.CreateDirectory(_queuesDir);
        Directory.CreateDirectory(_backupDir);

        var path = RegionPath(region.RegionId);

        // Backup текущей версии перед перезаписью
        if (File.Exists(path))
            File.Copy(path, Path.Combine(_backupDir, $"{region.RegionId}.json"), overwrite: true);

        var json = JsonSerializer.Serialize(region, JsonOpts);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    private string RegionPath(Guid regionId) => Path.Combine(_queuesDir, $"{regionId}.json");
}
