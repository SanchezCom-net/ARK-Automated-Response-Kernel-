using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

public sealed class StorageManager : IStorageManager
{
    private const string Component = "StorageManager";

    public event System.Action?       MacroIndexChanged;
    public event EventHandler<Guid>? MacroStatusChanged;

    private readonly ILogService   _logger;
    private readonly SemaphoreSlim _diskLock = new(1, 1);

    private readonly string _baseDir;
    private readonly string _betaDir;
    private readonly string _releaseDir;
    private readonly string _historyDir;
    private readonly string _queuesDir;
    private readonly string _queuesBackupDir;
    private readonly string _mapPath;
    private readonly string _mapTmpPath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented                   = true,
        PropertyNameCaseInsensitive     = true,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate
    };

    public StorageManager(ILogService logger)
        : this(logger, Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory) { }

    internal StorageManager(ILogService logger, string baseDir)
    {
        _logger          = logger;
        _baseDir         = baseDir;
        _betaDir         = Path.Combine(_baseDir, "macros", "beta");
        _releaseDir      = Path.Combine(_baseDir, "macros", "release");
        _historyDir      = Path.Combine(_baseDir, "macros", "history");
        _queuesDir       = Path.Combine(_baseDir, "queues");
        _queuesBackupDir = Path.Combine(_baseDir, "queues", "backup");
        _mapPath         = Path.Combine(_baseDir, "system_map.json");
        _mapTmpPath      = _mapPath + ".tmp";
    }

    // ── Инициализация ─────────────────────────────────────────────────────────

    public async Task EnsureDirectoriesAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_betaDir);
        Directory.CreateDirectory(_releaseDir);
        Directory.CreateDirectory(_historyDir);
        Directory.CreateDirectory(_queuesDir);
        Directory.CreateDirectory(_queuesBackupDir);

        if (!File.Exists(_mapPath))
            await RebuildSystemMapAsync(ct).ConfigureAwait(false);

        await _logger.LogInfoAsync(Component, "Структура хранилища проверена.").ConfigureAwait(false);
    }

    // ── CRUD макросов ─────────────────────────────────────────────────────────

    public async Task<MacroDocument> LoadMacroAsync(Guid id, CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try   { return await LoadMacroInternalAsync(id, ct).ConfigureAwait(false); }
        finally { _diskLock.Release(); }
    }

    public async Task SaveMacroAsync(MacroDocument doc, CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try   { await SaveMacroInternalAsync(doc, ct).ConfigureAwait(false); }
        finally { _diskLock.Release(); }
        MacroIndexChanged?.Invoke();
        MacroStatusChanged?.Invoke(this, doc.Id);
    }

    public async Task DeleteMacroAsync(Guid id, CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map      = await LoadMapInternalAsync(ct).ConfigureAwait(false);
            var manifest = map.Macros.FirstOrDefault(m => m.Id == id);
            if (manifest is null) return;

            var fullPath = Path.Combine(_baseDir, manifest.FilePath);
            if (File.Exists(fullPath)) File.Delete(fullPath);

            map.Macros.Remove(manifest);
            RemoveMacroFromAllFolders(map.Roots, id);
            await SaveMapInternalAsync(map, ct).ConfigureAwait(false);
        }
        finally { _diskLock.Release(); }
    }

    // ── Promote / Demote ──────────────────────────────────────────────────────

    public async Task PromoteToReleaseAsync(Guid id, CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map      = await LoadMapInternalAsync(ct).ConfigureAwait(false);
            var manifest = map.Macros.FirstOrDefault(m => m.Id == id)
                           ?? throw new KeyNotFoundException($"Макрос {id} не найден в system_map.");

            var src  = Path.Combine(_baseDir, manifest.FilePath);
            var dst  = Path.Combine(_releaseDir, $"{SanitizeName(manifest.Name)}_{id}.json");
            File.Copy(src, dst, overwrite: true);
            if (!src.Equals(dst, StringComparison.OrdinalIgnoreCase)) File.Delete(src);

            manifest.Environment = "release";
            manifest.FilePath    = Path.GetRelativePath(_baseDir, dst).Replace('\\', '/');
            await SaveMapInternalAsync(map, ct).ConfigureAwait(false);

            await _logger.LogInfoAsync(Component, $"Макрос '{manifest.Name}' повышен до release.").ConfigureAwait(false);
        }
        finally { _diskLock.Release(); }
        MacroIndexChanged?.Invoke();
        MacroStatusChanged?.Invoke(this, id);
    }

    public async Task DemoteToBetaAsync(Guid id, CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map      = await LoadMapInternalAsync(ct).ConfigureAwait(false);
            var manifest = map.Macros.FirstOrDefault(m => m.Id == id)
                           ?? throw new KeyNotFoundException($"Макрос {id} не найден в system_map.");

            var src       = Path.Combine(_baseDir, manifest.FilePath);
            var safeName  = SanitizeName(manifest.Name);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");

            File.Copy(src, Path.Combine(_historyDir, $"{safeName}_{id}_{timestamp}.json"));
            RotateHistory(id, safeName);

            var dst = Path.Combine(_betaDir, $"{safeName}_{id}.json");
            File.Move(src, dst, overwrite: true);

            manifest.Environment = "beta";
            manifest.FilePath    = Path.GetRelativePath(_baseDir, dst).Replace('\\', '/');
            await SaveMapInternalAsync(map, ct).ConfigureAwait(false);

            await _logger.LogInfoAsync(Component, $"Макрос '{manifest.Name}' понижен до beta.").ConfigureAwait(false);
        }
        finally { _diskLock.Release(); }
        MacroIndexChanged?.Invoke();
        MacroStatusChanged?.Invoke(this, id);
    }

    // ── Экспорт / Импорт ──────────────────────────────────────────────────────

    public async Task ExportMacroAsync(Guid id, string destinationPath, CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc  = await LoadMacroInternalAsync(id, ct).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(doc, JsonOpts);
            await File.WriteAllTextAsync(destinationPath, json, ct).ConfigureAwait(false);
            await _logger.LogInfoAsync(Component, $"Макрос '{doc.UserDefinedName}' экспортирован: {destinationPath}.").ConfigureAwait(false);
        }
        finally { _diskLock.Release(); }
    }

    public async Task<MacroDocument> ImportMacroAsync(
        string sourcePath, Guid? targetFolderId = null, CancellationToken ct = default)
    {
        // result объявлен ВНЕ try-finally, чтобы MacroStatusChanged можно было вызвать
        // ПОСЛЕ освобождения _diskLock — иначе подписчик (EventMonitor) дедлочится
        // при попытке вызвать GetAllMacrosAsync (WaitAsync на тот же SemaphoreSlim).
        MacroDocument result;
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = await File.ReadAllTextAsync(sourcePath, ct).ConfigureAwait(false);
            var doc  = JsonSerializer.Deserialize<MacroDocument>(json, JsonOpts)
                       ?? throw new InvalidDataException("Невалидный или пустой файл макроса.");

            // Разрешение конфликта имён
            var map      = await LoadMapInternalAsync(ct).ConfigureAwait(false);
            var baseName = doc.UserDefinedName;
            var suffix   = 1;
            var newName  = baseName;
            while (map.Macros.Any(m => m.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                newName = $"{baseName} ({suffix++})";

            result = new MacroDocument
            {
                Id              = Guid.NewGuid(),
                UserDefinedName = newName,
                Environment     = "beta",
                RegionId        = doc.RegionId,
                QueuePriority   = doc.QueuePriority,
                Macro           = DeepCopyWithNewNodeIds(doc.Macro)
            };

            await SaveMacroInternalAsync(result, ct).ConfigureAwait(false);

            if (targetFolderId.HasValue)
                await MoveMacroToFolderInternalAsync(result.Id, targetFolderId.Value, ct).ConfigureAwait(false);

            await _logger.LogInfoAsync(Component, $"Импортирован макрос '{result.UserDefinedName}'.").ConfigureAwait(false);
        }
        finally { _diskLock.Release(); }

        // После освобождения lock — безопасно оповещать подписчиков
        MacroStatusChanged?.Invoke(this, result.Id);
        return result;
    }

    // ── Дублирование (Deep Copy + Re-ID) ─────────────────────────────────────

    public async Task<MacroDocument> DuplicateMacroAsync(
        Guid sourceId, Guid? targetFolderId = null, CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 1. Загрузка источника
            var src = await LoadMacroInternalAsync(sourceId, ct).ConfigureAwait(false);

            // 2. Разрешение конфликта имён
            var map      = await LoadMapInternalAsync(ct).ConfigureAwait(false);
            var baseName = src.UserDefinedName;
            var newName  = baseName;
            var suffix   = 1;
            while (map.Macros.Any(m => m.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                newName = $"{baseName} ({suffix++})";

            // 3–5. Полная перегенерация ID нод и проводов
            var newMacro = DeepCopyWithNewNodeIds(src.Macro);

            // 6. Создаём и сохраняем как новый файл
            var result = new MacroDocument
            {
                Id              = Guid.NewGuid(),
                UserDefinedName = newName,
                Environment     = src.Environment,
                RegionId        = src.RegionId,
                QueuePriority   = src.QueuePriority,
                Macro           = newMacro
            };

            await SaveMacroInternalAsync(result, ct).ConfigureAwait(false);

            // 7. Помещаем в ту же папку или указанную
            var effectiveFolderId = targetFolderId ?? FindFolderContainingMacro(map.Roots, sourceId);
            if (effectiveFolderId.HasValue)
                await MoveMacroToFolderInternalAsync(result.Id, effectiveFolderId.Value, ct).ConfigureAwait(false);

            await _logger.LogInfoAsync(Component, $"Макрос '{src.UserDefinedName}' дублирован как '{newName}'.").ConfigureAwait(false);
            return result;
        }
        finally { _diskLock.Release(); }
    }

    // ── SystemMap / Virtual Tree ──────────────────────────────────────────────

    public async Task<IReadOnlyList<MacroManifest>> GetAllMacrosAsync(CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = await LoadMapInternalAsync(ct).ConfigureAwait(false);
            return map.Macros.AsReadOnly();
        }
        finally { _diskLock.Release(); }
    }

    public async Task<SystemMap> GetVirtualTreeAsync(CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try   { return await LoadMapInternalAsync(ct).ConfigureAwait(false); }
        finally { _diskLock.Release(); }
    }

    public async Task RebuildSystemMapAsync(CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try   { await RebuildInternalAsync(ct).ConfigureAwait(false); }
        finally { _diskLock.Release(); }
    }

    // ── Управление виртуальным деревом ────────────────────────────────────────

    public async Task<VirtualTreeNode> AddFolderAsync(
        string name, bool isAppFolder, Guid? parentFolderId = null, CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map    = await LoadMapInternalAsync(ct).ConfigureAwait(false);

            // Проверка уникальности имени на текущем уровне
            var siblingList = parentFolderId is null
                ? map.Roots
                : FindFolderWithChildren(map.Roots, parentFolderId.Value);
            if (siblingList?.Any(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == true)
                throw new InvalidOperationException($"Элемент с именем «{name}» уже существует на этом уровне.");

            VirtualTreeNode node = isAppFolder
                ? new AppFolderNode { Name = name }
                : new FolderNode    { Name = name };

            if (parentFolderId is null)
            {
                map.Roots.Add(node);
            }
            else
            {
                var parent = FindFolderWithChildren(map.Roots, parentFolderId.Value);
                if (parent is null) throw new KeyNotFoundException($"Папка {parentFolderId} не найдена.");
                parent.Add(node);
            }

            await SaveMapInternalAsync(map, ct).ConfigureAwait(false);
            await _logger.LogInfoAsync(Component, $"Создана папка '{name}' (AppFolder={isAppFolder}).").ConfigureAwait(false);
            return node;
        }
        finally { _diskLock.Release(); }
    }

    public async Task DeleteFolderAsync(Guid folderId, CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = await LoadMapInternalAsync(ct).ConfigureAwait(false);

            // Собираем все макросы в папке (рекурсивно) для удаления с диска
            var macroIds = new List<Guid>();
            CollectMacroIdsFromFolder(map.Roots, folderId, macroIds);

            foreach (var id in macroIds)
            {
                var m = map.Macros.FirstOrDefault(x => x.Id == id);
                if (m is null) continue;
                var path = Path.Combine(_baseDir, m.FilePath);
                if (File.Exists(path)) File.Delete(path);
                map.Macros.Remove(m);
            }

            RemoveFolderFromTree(map.Roots, folderId);
            await SaveMapInternalAsync(map, ct).ConfigureAwait(false);
            await _logger.LogInfoAsync(Component, $"Папка {folderId} удалена вместе с {macroIds.Count} макросами.").ConfigureAwait(false);
        }
        finally { _diskLock.Release(); }
    }

    public async Task RenameFolderAsync(Guid folderId, string newName, CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map    = await LoadMapInternalAsync(ct).ConfigureAwait(false);
            var folder = FindNodeById(map.Roots, folderId)
                         ?? throw new KeyNotFoundException($"Папка {folderId} не найдена.");

            // Проверка уникальности нового имени среди соседей (исключая себя)
            var siblings = FindSiblingList(map.Roots, folderId);
            if (siblings?.Any(n => n.Id != folderId && n.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)) == true)
                throw new InvalidOperationException($"Элемент с именем «{newName}» уже существует на этом уровне.");

            folder.Name = newName;
            await SaveMapInternalAsync(map, ct).ConfigureAwait(false);
        }
        finally { _diskLock.Release(); }
    }

    public async Task UpdateAppFolderBindingAsync(Guid folderId, ContextBinding binding, CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map    = await LoadMapInternalAsync(ct).ConfigureAwait(false);
            var folder = FindNodeById(map.Roots, folderId) as AppFolderNode
                         ?? throw new KeyNotFoundException($"AppFolder {folderId} не найден.");
            folder.Binding = binding;
            await SaveMapInternalAsync(map, ct).ConfigureAwait(false);
            await _logger.LogInfoAsync(Component, $"Обновлена привязка AppFolder '{folder.Name}'.").ConfigureAwait(false);
        }
        finally { _diskLock.Release(); }
    }

    public async Task MoveMacroToFolderAsync(Guid macroId, Guid? targetFolderId, CancellationToken ct = default)
    {
        await _diskLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = await LoadMapInternalAsync(ct).ConfigureAwait(false);
            RemoveMacroFromAllFolders(map.Roots, macroId);

            if (targetFolderId.HasValue)
            {
                var target = FindFolderWithMacroIds(map.Roots, targetFolderId.Value);
                if (target is null) throw new KeyNotFoundException($"Папка {targetFolderId} не найдена.");
                target.Add(macroId);
            }

            await SaveMapInternalAsync(map, ct).ConfigureAwait(false);
        }
        finally { _diskLock.Release(); }
    }

    // ── Внутренние методы ─────────────────────────────────────────────────────

    private async Task<MacroDocument> LoadMacroInternalAsync(Guid id, CancellationToken ct)
    {
        var map      = await LoadMapInternalAsync(ct).ConfigureAwait(false);
        var manifest = map.Macros.FirstOrDefault(m => m.Id == id)
                       ?? throw new FileNotFoundException($"Макрос {id} не найден в system_map.");
        var path = Path.Combine(_baseDir, manifest.FilePath);
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<MacroDocument>(json, JsonOpts)
               ?? throw new InvalidDataException($"Повреждён файл макроса: {path}");
    }

    private async Task SaveMacroInternalAsync(MacroDocument doc, CancellationToken ct)
    {
        var dir      = doc.Environment == "release" ? _releaseDir : _betaDir;
        var fileName = $"{SanitizeName(doc.UserDefinedName)}_{doc.Id}.json";
        var fullPath = Path.Combine(dir, fileName);
        var tmpPath  = fullPath + ".tmp";

        var json = JsonSerializer.Serialize(doc, JsonOpts);
        await File.WriteAllTextAsync(tmpPath, json, ct).ConfigureAwait(false);

        if (File.Exists(fullPath)) File.Replace(tmpPath, fullPath, null);
        else File.Move(tmpPath, fullPath);

        var map      = await LoadMapInternalAsync(ct).ConfigureAwait(false);
        var rel      = Path.GetRelativePath(_baseDir, fullPath).Replace('\\', '/');
        var existing = map.Macros.FirstOrDefault(m => m.Id == doc.Id);
        if (existing is not null)
        {
            existing.Name          = doc.UserDefinedName;
            existing.Environment   = doc.Environment;
            existing.FilePath      = rel;
            existing.RegionId      = doc.RegionId;
            existing.QueuePriority = doc.QueuePriority;
        }
        else
        {
            map.Macros.Add(new MacroManifest
            {
                Id            = doc.Id,
                Name          = doc.UserDefinedName,
                Environment   = doc.Environment,
                FilePath      = rel,
                RegionId      = doc.RegionId,
                QueuePriority = doc.QueuePriority
            });
        }
        await SaveMapInternalAsync(map, ct).ConfigureAwait(false);
    }

    // Перемещает макрос в папку без захвата лока (вызывается внутри уже захваченного лока)
    private async Task MoveMacroToFolderInternalAsync(Guid macroId, Guid targetFolderId, CancellationToken ct)
    {
        var map = await LoadMapInternalAsync(ct).ConfigureAwait(false);
        RemoveMacroFromAllFolders(map.Roots, macroId);
        var target = FindFolderWithMacroIds(map.Roots, targetFolderId);
        target?.Add(macroId);
        await SaveMapInternalAsync(map, ct).ConfigureAwait(false);
    }

    private async Task<SystemMap> LoadMapInternalAsync(CancellationToken ct)
    {
        if (!File.Exists(_mapPath))
        {
            await RebuildInternalAsync(ct).ConfigureAwait(false);
        }
        try
        {
            var json = await File.ReadAllTextAsync(_mapPath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SystemMap>(json, JsonOpts) ?? new SystemMap();
        }
        catch
        {
            await _logger.LogWarningAsync(Component, "system_map.json повреждён — Auto-Rebuild.").ConfigureAwait(false);
            await RebuildInternalAsync(ct).ConfigureAwait(false);
            var json = await File.ReadAllTextAsync(_mapPath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SystemMap>(json, JsonOpts) ?? new SystemMap();
        }
    }

    private async Task SaveMapInternalAsync(SystemMap map, CancellationToken ct)
    {
        map.LastUpdated = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(map, JsonOpts);
        await File.WriteAllTextAsync(_mapTmpPath, json, ct).ConfigureAwait(false);
        if (File.Exists(_mapPath)) File.Replace(_mapTmpPath, _mapPath, null);
        else File.Move(_mapTmpPath, _mapPath);
    }

    private async Task RebuildInternalAsync(CancellationToken ct)
    {
        var map = new SystemMap();
        await ScanDirIntoMapAsync(_betaDir,    "beta",    map, ct).ConfigureAwait(false);
        await ScanDirIntoMapAsync(_releaseDir, "release", map, ct).ConfigureAwait(false);
        await SaveMapInternalAsync(map, ct).ConfigureAwait(false);
        await _logger.LogInfoAsync(Component, $"system_map восстановлен: {map.Macros.Count} макросов.").ConfigureAwait(false);
    }

    private async Task ScanDirIntoMapAsync(string dir, string env, SystemMap map, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var doc  = JsonSerializer.Deserialize<MacroDocument>(json, JsonOpts);
                if (doc is null) continue;
                if (map.Macros.Any(m => m.Id == doc.Id)) continue;
                map.Macros.Add(new MacroManifest
                {
                    Id            = doc.Id,
                    Name          = doc.UserDefinedName,
                    Environment   = string.IsNullOrEmpty(doc.Environment) ? env : doc.Environment,
                    FilePath      = Path.GetRelativePath(_baseDir, file).Replace('\\', '/'),
                    RegionId      = doc.RegionId,
                    QueuePriority = doc.QueuePriority
                });
            }
            catch (Exception ex)
            {
                await _logger.LogErrorAsync(Component,
                    $"Пропущен повреждённый файл: {Path.GetFileName(file)}", ex).ConfigureAwait(false);
            }
        }
    }

    // ── Deep Copy: перегенерация ID нод и проводов ───────────────────────────

    /// <summary>
    /// Полная перегенерация ID:
    /// 1. Строит словарь oldId → newId для всех нод.
    /// 2. Заменяет все вхождения в сериализованном JSON.
    /// 3. Покрывает NodeId, SourceNodeId, TargetNodeId, StartNodeId.
    /// </summary>
    private static MacroEntry DeepCopyWithNewNodeIds(MacroEntry macro)
    {
        var idMap = new Dictionary<string, string>();
        foreach (var vn in macro.VisualNodes)
            idMap[vn.NodeId.ToString("D")] = Guid.NewGuid().ToString("D");

        if (idMap.Count == 0) return macro;

        var json = JsonSerializer.Serialize(macro, JsonOpts);
        foreach (var (oldId, newId) in idMap)
            json = json.Replace($"\"{oldId}\"", $"\"{newId}\"", StringComparison.Ordinal);

        return JsonSerializer.Deserialize<MacroEntry>(json, JsonOpts) ?? macro;
    }

    // ── Вспомогательные методы для работы с деревом ──────────────────────────

    private static void RemoveMacroFromAllFolders(List<VirtualTreeNode> nodes, Guid macroId)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case AppFolderNode app:
                    app.MacroIds.Remove(macroId);
                    RemoveMacroFromAllFolders(app.Children, macroId);
                    break;
                case FolderNode folder:
                    folder.MacroIds.Remove(macroId);
                    RemoveMacroFromAllFolders(folder.Children, macroId);
                    break;
            }
        }
    }

    private static void RemoveFolderFromTree(List<VirtualTreeNode> nodes, Guid folderId)
    {
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            if (nodes[i].Id == folderId) { nodes.RemoveAt(i); return; }
            switch (nodes[i])
            {
                case AppFolderNode app:    RemoveFolderFromTree(app.Children,    folderId); break;
                case FolderNode folder:    RemoveFolderFromTree(folder.Children, folderId); break;
            }
        }
    }

    private static VirtualTreeNode? FindNodeById(List<VirtualTreeNode> nodes, Guid id)
    {
        foreach (var n in nodes)
        {
            if (n.Id == id) return n;
            var found = n switch
            {
                AppFolderNode app => FindNodeById(app.Children, id),
                FolderNode folder => FindNodeById(folder.Children, id),
                _                 => null
            };
            if (found is not null) return found;
        }
        return null;
    }

    // Возвращает список (Roots или Children), содержащий узел с заданным ID
    private static List<VirtualTreeNode>? FindSiblingList(List<VirtualTreeNode> nodes, Guid nodeId)
    {
        if (nodes.Any(n => n.Id == nodeId)) return nodes;
        foreach (var n in nodes)
        {
            var children = n switch
            {
                AppFolderNode app => app.Children,
                FolderNode folder => folder.Children,
                _                 => null
            };
            if (children is null) continue;
            var found = FindSiblingList(children, nodeId);
            if (found is not null) return found;
        }
        return null;
    }

    // Возвращает дочерние узлы папки (для добавления дочерней папки)
    private static List<VirtualTreeNode>? FindFolderWithChildren(List<VirtualTreeNode> nodes, Guid folderId)
    {
        foreach (var n in nodes)
        {
            switch (n)
            {
                case AppFolderNode app when app.Id == folderId: return app.Children;
                case FolderNode folder when folder.Id == folderId: return folder.Children;
                case AppFolderNode app:
                    var r1 = FindFolderWithChildren(app.Children, folderId);
                    if (r1 is not null) return r1;
                    break;
                case FolderNode folder:
                    var r2 = FindFolderWithChildren(folder.Children, folderId);
                    if (r2 is not null) return r2;
                    break;
            }
        }
        return null;
    }

    // Возвращает список MacroIds папки (для добавления макроса в папку)
    private static List<Guid>? FindFolderWithMacroIds(List<VirtualTreeNode> nodes, Guid folderId)
    {
        foreach (var n in nodes)
        {
            switch (n)
            {
                case AppFolderNode app when app.Id == folderId: return app.MacroIds;
                case FolderNode folder when folder.Id == folderId: return folder.MacroIds;
                case AppFolderNode app:
                    var r1 = FindFolderWithMacroIds(app.Children, folderId);
                    if (r1 is not null) return r1;
                    break;
                case FolderNode folder:
                    var r2 = FindFolderWithMacroIds(folder.Children, folderId);
                    if (r2 is not null) return r2;
                    break;
            }
        }
        return null;
    }

    private static Guid? FindFolderContainingMacro(List<VirtualTreeNode> nodes, Guid macroId)
    {
        foreach (var n in nodes)
        {
            switch (n)
            {
                case AppFolderNode app:
                    if (app.MacroIds.Contains(macroId)) return app.Id;
                    var r1 = FindFolderContainingMacro(app.Children, macroId);
                    if (r1.HasValue) return r1;
                    break;
                case FolderNode folder:
                    if (folder.MacroIds.Contains(macroId)) return folder.Id;
                    var r2 = FindFolderContainingMacro(folder.Children, macroId);
                    if (r2.HasValue) return r2;
                    break;
            }
        }
        return null;
    }

    private static void CollectMacroIdsFromFolder(List<VirtualTreeNode> nodes, Guid folderId, List<Guid> result)
    {
        foreach (var n in nodes)
        {
            if (n.Id == folderId)
            {
                CollectAllMacroIds(new List<VirtualTreeNode> { n }, result);
                return;
            }
            switch (n)
            {
                case AppFolderNode app:    CollectMacroIdsFromFolder(app.Children,    folderId, result); break;
                case FolderNode folder:    CollectMacroIdsFromFolder(folder.Children, folderId, result); break;
            }
        }
    }

    private static void CollectAllMacroIds(List<VirtualTreeNode> nodes, List<Guid> result)
    {
        foreach (var n in nodes)
        {
            switch (n)
            {
                case AppFolderNode app:
                    result.AddRange(app.MacroIds);
                    CollectAllMacroIds(app.Children, result);
                    break;
                case FolderNode folder:
                    result.AddRange(folder.MacroIds);
                    CollectAllMacroIds(folder.Children, result);
                    break;
            }
        }
    }

    // ── Статические хелперы ───────────────────────────────────────────────────

    private void RotateHistory(Guid id, string safeName)
    {
        var files = Directory.GetFiles(_historyDir, $"{safeName}_{id}_*.json")
                             .OrderBy(f => f).ToList();
        while (files.Count > 2)
        {
            File.Delete(files[0]);
            files.RemoveAt(0);
        }
    }

    private static string SanitizeName(string name)
        => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')).Trim('_');
}
