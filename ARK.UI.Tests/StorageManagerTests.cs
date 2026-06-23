using System.IO;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Services;
using NSubstitute;

namespace ARK.UI.Tests;

public sealed class StorageManagerTests : IAsyncDisposable
{
    private readonly string         _tempDir;
    private readonly ILogService    _log;
    private readonly StorageManager _sut;

    public StorageManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ARK_Tests_{Guid.NewGuid():N}");
        _log     = Substitute.For<ILogService>();
        _log.LogInfoAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        _log.LogWarningAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        _log.LogErrorAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Exception?>()).Returns(Task.CompletedTask);
        _sut = new StorageManager(_log, _tempDir);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        return ValueTask.CompletedTask;
    }

    // ── Happy Path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureDirectoriesAsync_CreatesAllRequiredFolders()
    {
        await _sut.EnsureDirectoriesAsync();

        Assert.True(Directory.Exists(Path.Combine(_tempDir, "macros", "beta")));
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "macros", "release")));
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "macros", "history")));
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "queues")));
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "queues", "backup")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "system_map.json")));
    }

    [Fact]
    public async Task SaveAndLoadMacroAsync_ReturnsEqualDocument()
    {
        await _sut.EnsureDirectoriesAsync();

        var doc = new MacroDocument
        {
            Id              = Guid.NewGuid(),
            UserDefinedName = "ТестМакрос",
            Environment     = "beta",
            QueuePriority   = 5
        };

        await _sut.SaveMacroAsync(doc);
        var loaded = await _sut.LoadMacroAsync(doc.Id);

        Assert.Equal(doc.Id,              loaded.Id);
        Assert.Equal(doc.UserDefinedName, loaded.UserDefinedName);
        Assert.Equal(doc.Environment,     loaded.Environment);
        Assert.Equal(doc.QueuePriority,   loaded.QueuePriority);
    }

    [Fact]
    public async Task SaveMacroAsync_UpdatesSystemMap()
    {
        await _sut.EnsureDirectoriesAsync();

        var doc = new MacroDocument { Id = Guid.NewGuid(), UserDefinedName = "МакросКарта" };
        await _sut.SaveMacroAsync(doc);

        var all = await _sut.GetAllMacrosAsync();
        Assert.Single(all, m => m.Id == doc.Id && m.Name == "МакросКарта");
    }

    [Fact]
    public async Task DeleteMacroAsync_RemovesFileAndManifest()
    {
        await _sut.EnsureDirectoriesAsync();

        var doc = new MacroDocument { Id = Guid.NewGuid(), UserDefinedName = "УдалитьМеня" };
        await _sut.SaveMacroAsync(doc);
        await _sut.DeleteMacroAsync(doc.Id);

        var all = await _sut.GetAllMacrosAsync();
        Assert.DoesNotContain(all, m => m.Id == doc.Id);
    }

    [Fact]
    public async Task PromoteToReleaseAsync_MovesFileAndUpdatesEnvironment()
    {
        await _sut.EnsureDirectoriesAsync();

        var doc = new MacroDocument { Id = Guid.NewGuid(), UserDefinedName = "ПромоутМакрос", Environment = "beta" };
        await _sut.SaveMacroAsync(doc);

        await _sut.PromoteToReleaseAsync(doc.Id);

        var manifest = (await _sut.GetAllMacrosAsync()).First(m => m.Id == doc.Id);
        Assert.Equal("release", manifest.Environment);
        Assert.False(File.Exists(Path.Combine(_tempDir, "macros", "beta", $"ПромоутМакрос_{doc.Id}.json")));
        Assert.True(File.Exists(Path.Combine(_tempDir, manifest.FilePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task DemoteToBetaAsync_ArchivesToHistoryAndMovesToBeta()
    {
        await _sut.EnsureDirectoriesAsync();

        var doc = new MacroDocument { Id = Guid.NewGuid(), UserDefinedName = "ДемоутМакрос", Environment = "release" };
        // Сохраняем напрямую в release
        doc.Environment = "release";
        await _sut.SaveMacroAsync(doc);

        await _sut.DemoteToBetaAsync(doc.Id);

        var manifest = (await _sut.GetAllMacrosAsync()).First(m => m.Id == doc.Id);
        Assert.Equal("beta", manifest.Environment);

        // Проверяем что история не пуста
        var histFiles = Directory.GetFiles(Path.Combine(_tempDir, "macros", "history"), $"*{doc.Id}*.json");
        Assert.NotEmpty(histFiles);
    }

    [Fact]
    public async Task RebuildSystemMapAsync_RecoversMacrosFromDisk()
    {
        await _sut.EnsureDirectoriesAsync();

        // Сохраняем несколько макросов
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids)
            await _sut.SaveMacroAsync(new MacroDocument { Id = id, UserDefinedName = $"Макрос_{id.ToString("N")[..8]}" });

        // Удаляем system_map.json для симуляции сбоя
        File.Delete(Path.Combine(_tempDir, "system_map.json"));

        await _sut.RebuildSystemMapAsync();

        var all = await _sut.GetAllMacrosAsync();
        Assert.Equal(ids.Length, all.Count);
    }

    [Fact]
    public async Task ImportMacroAsync_ConflictingName_AddsSuffix()
    {
        await _sut.EnsureDirectoriesAsync();

        var original = new MacroDocument { Id = Guid.NewGuid(), UserDefinedName = "Конфликт" };
        await _sut.SaveMacroAsync(original);

        // Экспортируем для последующего импорта
        var exportPath = Path.Combine(_tempDir, "export.json");
        await _sut.ExportMacroAsync(original.Id, exportPath);

        var imported = await _sut.ImportMacroAsync(exportPath);

        Assert.StartsWith("Конфликт (", imported.UserDefinedName);
    }

    // ── V3 Compliance Tests ───────────────────────────────────────────────────

    /// <summary>
    /// Atomic Replace: запись идёт во временный .tmp, затем File.Replace.
    /// Убеждаемся что корректный документ доступен даже при параллельных сохранениях.
    /// </summary>
    [Fact]
    public async Task AtomicReplace_ShouldNotCorruptData_WhenInterrupted()
    {
        await _sut.EnsureDirectoriesAsync();

        var id  = Guid.NewGuid();
        var doc = new MacroDocument { Id = id, UserDefinedName = "АтомикТест", Environment = "beta" };

        // Первое сохранение
        await _sut.SaveMacroAsync(doc);

        // Симулируем прерванную запись: создаём висящий .tmp рядом с файлом
        var betaDir  = Path.Combine(_tempDir, "macros", "beta");
        var tmpFiles = Directory.GetFiles(betaDir, "*.tmp");
        // tmp уже удалён File.Move — создаём его вручную с мусором
        var tmpPath  = Path.Combine(betaDir, $"corrupted_{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tmpPath, "{{invalid json}}");

        // Второе сохранение должно завершиться успешно, невзирая на мусорный .tmp
        doc.UserDefinedName = "АтомикТест_V2";
        await _sut.SaveMacroAsync(doc);

        var loaded = await _sut.LoadMacroAsync(id);
        Assert.Equal("АтомикТест_V2", loaded.UserDefinedName);

        // Файл .tmp после успешной записи не должен оставаться рядом с основным файлом
        var lingering = Directory.GetFiles(betaDir, $"*_{id}.json.tmp");
        Assert.Empty(lingering);
    }

    /// <summary>
    /// AutoRebuild: удаляем system_map.json, создаём макросы в beta/ и release/ вручную,
    /// вызываем RebuildSystemMapAsync — карта должна восстановиться со всеми макросами.
    /// </summary>
    [Fact]
    public async Task AutoRebuild_ShouldRestoreSystemMap_FromMacroFiles()
    {
        await _sut.EnsureDirectoriesAsync();

        // Создаём 1 beta и 1 release макрос через StorageManager (нормальный путь)
        var betaDoc = new MacroDocument
        {
            Id              = Guid.NewGuid(),
            UserDefinedName = "BetaMacro",
            Environment     = "beta"
        };
        var relDoc = new MacroDocument
        {
            Id              = Guid.NewGuid(),
            UserDefinedName = "ReleaseMacro",
            Environment     = "release"
        };
        await _sut.SaveMacroAsync(betaDoc);
        await _sut.SaveMacroAsync(relDoc);

        // Удаляем system_map.json — симуляция сбоя
        var mapPath = Path.Combine(_tempDir, "system_map.json");
        File.Delete(mapPath);
        Assert.False(File.Exists(mapPath));

        // Восстанавливаем карту
        await _sut.RebuildSystemMapAsync();

        // Карта должна снова существовать и содержать оба макроса
        Assert.True(File.Exists(mapPath));
        var all = await _sut.GetAllMacrosAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, m => m.Id == betaDoc.Id && m.Environment == "beta");
        Assert.Contains(all, m => m.Id == relDoc.Id  && m.Environment == "release");
    }

    /// <summary>
    /// Promote → Demote цикл жизни с проверкой ротации истории (не более 2 файлов).
    /// </summary>
    [Fact]
    public async Task PromoteAndDemote_ShouldMoveFilesAndRotateHistory()
    {
        await _sut.EnsureDirectoriesAsync();

        var id  = Guid.NewGuid();
        var doc = new MacroDocument
        {
            Id              = id,
            UserDefinedName = "LifecycleMacro",
            Environment     = "beta"
        };
        await _sut.SaveMacroAsync(doc);

        // Promote: beta → release
        await _sut.PromoteToReleaseAsync(id);
        var afterPromote = (await _sut.GetAllMacrosAsync()).First(m => m.Id == id);
        Assert.Equal("release", afterPromote.Environment);
        Assert.False(File.Exists(Path.Combine(_tempDir, "macros", "beta",
            $"LifecycleMacro_{id}.json")));

        // Demote #1: release → beta + архив в history
        await _sut.DemoteToBetaAsync(id);
        var afterDemote1 = (await _sut.GetAllMacrosAsync()).First(m => m.Id == id);
        Assert.Equal("beta", afterDemote1.Environment);

        var histDir   = Path.Combine(_tempDir, "macros", "history");
        var histFiles = Directory.GetFiles(histDir, $"*{id}*.json");
        Assert.Single(histFiles);

        // Promote снова, затем Demote #2 и #3 — ротация должна оставить не более 2 файлов
        await _sut.PromoteToReleaseAsync(id);
        await _sut.DemoteToBetaAsync(id);

        await _sut.PromoteToReleaseAsync(id);
        await _sut.DemoteToBetaAsync(id);

        var histAfterRotation = Directory.GetFiles(histDir, $"*{id}*.json");
        Assert.True(histAfterRotation.Length <= 2,
            $"Ожидалось ≤2 файлов в history, найдено: {histAfterRotation.Length}");
    }

    /// <summary>
    /// ThreadSafety: 10 параллельных записей system_map — SemaphoreSlim(1,1) должен
    /// предотвратить IOException и гарантировать консистентность карты.
    /// </summary>
    [Fact]
    public async Task ThreadSafety_SemaphoreSlim_PreventsRaceConditions()
    {
        await _sut.EnsureDirectoriesAsync();

        const int taskCount = 10;
        var tasks = Enumerable.Range(0, taskCount).Select(i =>
            _sut.SaveMacroAsync(new MacroDocument
            {
                Id              = Guid.NewGuid(),
                UserDefinedName = $"RaceCondition_{i}",
                Environment     = "beta"
            })).ToArray();

        // Не должно быть никаких исключений
        var ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
        Assert.Null(ex);

        // Карта должна содержать ровно taskCount макросов
        var all = await _sut.GetAllMacrosAsync();
        Assert.Equal(taskCount, all.Count);
    }
}
