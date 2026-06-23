using ARK.UI.Core.Models;

namespace ARK.UI.Core.Interfaces;

public interface IStorageManager
{
    /// <summary>Создаёт структуру папок и инициализирует system_map.json при первом запуске.</summary>
    Task EnsureDirectoriesAsync(CancellationToken ct = default);

    Task<MacroDocument> LoadMacroAsync(Guid id, CancellationToken ct = default);
    Task SaveMacroAsync(MacroDocument doc, CancellationToken ct = default);
    Task DeleteMacroAsync(Guid id, CancellationToken ct = default);

    /// <summary>beta → release: копирует файл, обновляет system_map.</summary>
    Task PromoteToReleaseAsync(Guid id, CancellationToken ct = default);

    /// <summary>release → beta: архивирует в history (макс. 2 версии), обновляет system_map.</summary>
    Task DemoteToBetaAsync(Guid id, CancellationToken ct = default);

    Task ExportMacroAsync(Guid id, string destinationPath, CancellationToken ct = default);

    /// <summary>
    /// Импортирует .arkmacro файл. Перегенерирует ID нод при конфликте;
    /// добавляет постфикс "(N)" при совпадении имени; помещает в targetFolderId.
    /// </summary>
    Task<MacroDocument> ImportMacroAsync(string sourcePath, Guid? targetFolderId = null, CancellationToken ct = default);

    /// <summary>Глубокая копия макроса с полной перегенерацией всех ID нод и проводов.</summary>
    Task<MacroDocument> DuplicateMacroAsync(Guid sourceId, Guid? targetFolderId = null, CancellationToken ct = default);

    Task<IReadOnlyList<MacroManifest>> GetAllMacrosAsync(CancellationToken ct = default);

    /// <summary>Возвращает полное виртуальное дерево (SystemMap с Roots + Macros).</summary>
    Task<SystemMap> GetVirtualTreeAsync(CancellationToken ct = default);

    // ── Управление виртуальным деревом ────────────────────────────────────

    Task<VirtualTreeNode> AddFolderAsync(string name, bool isAppFolder, Guid? parentFolderId = null, CancellationToken ct = default);
    Task DeleteFolderAsync(Guid folderId, CancellationToken ct = default);
    Task RenameFolderAsync(Guid folderId, string newName, CancellationToken ct = default);
    Task UpdateAppFolderBindingAsync(Guid folderId, ContextBinding binding, CancellationToken ct = default);

    /// <summary>Перемещает макрос в папку виртуального дерева (null = без папки).</summary>
    Task MoveMacroToFolderAsync(Guid macroId, Guid? targetFolderId, CancellationToken ct = default);

    /// <summary>Восстанавливает system_map.json сканируя папки beta и release.</summary>
    Task RebuildSystemMapAsync(CancellationToken ct = default);

    /// <summary>
    /// Срабатывает после Save, Promote, Demote или Import — EventMonitor перестраивает кэш триггеров.
    /// Fire-and-forget из фонового потока: подписчик не должен блокировать вызов.
    /// </summary>
    event System.Action? MacroIndexChanged;

    /// <summary>
    /// Расширенное событие с ID изменённого макроса.
    /// Срабатывает после Save, Promote, Demote, Import — всегда ПОСЛЕ освобождения _diskLock.
    /// </summary>
    event EventHandler<Guid>? MacroStatusChanged;
}
