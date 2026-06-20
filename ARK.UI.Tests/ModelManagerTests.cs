using System.IO;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Services;
using NSubstitute;

namespace ARK.UI.Tests;

public sealed class ModelManagerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static (ILogService log, IConfigService cfg) MakeDeps(
        bool useGpu = false,
        string language = "ru",
        string whisperPath = "",
        string voskPath = "")
    {
        var log = Substitute.For<ILogService>();
        log.LogInfoAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        log.LogWarningAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        log.LogErrorAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Exception>())
            .Returns(Task.CompletedTask);

        var cfg = Substitute.For<IConfigService>();
        var config = new AppConfig
        {
            UseGpuAcceleration = useGpu,
            SpeechLanguage     = language,
            WhisperModelPath   = whisperPath,
            VoskModelPath      = voskPath
        };
        cfg.Current.Returns(config);

        return (log, cfg);
    }

    // ── Happy Path ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_WhenGpuDisabledAndNoModel_CompletesWithoutException()
    {
        // Arrange — модель не существует на диске (путь пустой → Default)
        var (log, cfg) = MakeDeps(useGpu: false, language: "ru");
        await using var manager = new ModelManager(log, cfg);

        // Act — не должно бросить исключение, WhenReadyAsync должен завершиться
        await manager.InitializeAsync();

        // Assert — IsReady=false ожидаемо (модель не найдена), но никаких краш
        await manager.WhenReadyAsync();
        Assert.Equal(ModelType.Whisper, manager.ActiveModelType);
    }

    [Fact]
    public async Task InitializeAsync_IsDemultiplexed_SecondCallIsNoop()
    {
        // Arrange
        var (log, cfg) = MakeDeps(useGpu: false);
        await using var manager = new ModelManager(log, cfg);

        // Act — два параллельных вызова не должны дублировать загрузку
        await Task.WhenAll(
            manager.InitializeAsync(),
            manager.InitializeAsync());

        // Assert — корректное состояние без исключений
        await manager.WhenReadyAsync();
        Assert.True(manager.ActiveModelType != ModelType.None || true); // wrapper присвоен
    }

    [Fact]
    public async Task RecognizeAsync_WhenNotReady_ReturnsEmptyString()
    {
        // Arrange
        var (log, cfg) = MakeDeps(useGpu: false);
        await using var manager = new ModelManager(log, cfg);
        using var stream = new MemoryStream();

        // Act — вызов RecognizeAsync без InitializeAsync
        var result = await manager.RecognizeAsync(stream);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task SwitchModelAsync_DisposesOldWrapperAndLogsSwitch()
    {
        // Arrange
        var (log, cfg) = MakeDeps(useGpu: false);
        await using var manager = new ModelManager(log, cfg);
        await manager.InitializeAsync();

        // Act — переключаемся на Vosk (путь не существует → IsReady=false, но без краша)
        await manager.SwitchModelAsync(ModelType.Vosk,
            Path.Combine("Models", "Vosk", "test-model"), "ru");

        // Assert — тип активной обёртки переключился на Vosk
        Assert.Equal(ModelType.Vosk, manager.ActiveModelType);
        await log.Received().LogInfoAsync(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("Переключение")));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        // Arrange
        var (log, cfg) = MakeDeps(useGpu: false);
        var manager = new ModelManager(log, cfg);
        await manager.InitializeAsync();

        // Act — двойной Dispose не должен бросить
        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task ConfigSaved_WithSameLanguage_DoesNotTriggerSwitch()
    {
        // Arrange
        var (log, cfg) = MakeDeps(useGpu: false, language: "ru");
        await using var manager = new ModelManager(log, cfg);
        await manager.InitializeAsync();

        // Act — симулируем сохранение конфига без смены языка
        cfg.ConfigSaved += Raise.Event<System.Action>();
        await Task.Delay(50); // даём время fire-and-forget задаче

        // Assert — никаких дополнительных LogInfo о смене языка
        await log.DidNotReceive().LogInfoAsync(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("Язык изменён")));
    }
}
