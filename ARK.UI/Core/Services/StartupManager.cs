using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Services;

/// <summary>
/// Ленивая очередь инициализации — разгружает холодный старт приложения.
/// Все фазы выполняются строго в пуле потоков — UI-поток не блокируется.
/// </summary>
public sealed class StartupManager
{
    private const string Component = nameof(StartupManager);

    private readonly ILogService            _logger;
    private readonly ISpeechTriggerService  _speech;
    private readonly IConfigService         _config;
    private readonly IStorageManager        _storage;

    public StartupManager(
        ILogService            logger,
        ISpeechTriggerService  speech,
        IConfigService         config,
        IStorageManager        storage)
    {
        _logger  = logger;
        _speech  = speech;
        _config  = config;
        _storage = storage;
    }

    public void BeginLazyInitialization()
        => _ = Task.Run(RunInitQueueAsync);

    private async Task RunInitQueueAsync()
    {
        await InitGpuDiagnosticsAsync().ConfigureAwait(false);

        await Task.Delay(500).ConfigureAwait(false);
        await InitSpeechAsync().ConfigureAwait(false);

        await Task.Delay(1000).ConfigureAwait(false);
        await InitStorageAsync().ConfigureAwait(false);

        await Task.Delay(1500).ConfigureAwait(false);
        await InitRemainingAsync().ConfigureAwait(false);
    }

    private async Task InitGpuDiagnosticsAsync()
    {
        try
        {
            await CudaDiagnostics.CheckCompatibilityAsync(_logger).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(Component,
                $"Startup: GPU-диагностика завершилась с ошибкой: {ex.Message}. Продолжаем запуск.")
                .ConfigureAwait(false);
        }
    }

    private async Task InitSpeechAsync()
    {
        try
        {
            await _speech.InitializeAsync().ConfigureAwait(false);

            if (_config.Current.SpeechEnabled)
                await _speech.StartAsync().ConfigureAwait(false);
            else
                await _speech.StartMonitoringAsync().ConfigureAwait(false);

            await _logger.LogInfoAsync(Component,
                "Startup: SpeechTriggerService инициализирован.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                "Startup: Ошибка инициализации SpeechTriggerService.", ex).ConfigureAwait(false);
        }
    }

    private async Task InitStorageAsync()
    {
        try
        {
            await _storage.EnsureDirectoriesAsync().ConfigureAwait(false);
            var macros = await _storage.GetAllMacrosAsync().ConfigureAwait(false);

            await _logger.LogInfoAsync(Component,
                $"Startup: StorageManager инициализирован. Макросов в индексе: {macros.Count}.")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                "Startup: Ошибка инициализации StorageManager.", ex).ConfigureAwait(false);
        }
    }

    private async Task InitRemainingAsync()
    {
        try
        {
            await _logger.LogInfoAsync(Component,
                "Startup: Все фоновые модули инициализированы. Приложение полностью готово.")
                .ConfigureAwait(false);
        }
        catch { /* silent */ }
    }
}
