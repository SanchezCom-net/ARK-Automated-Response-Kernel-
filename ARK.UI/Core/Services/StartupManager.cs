using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Services;

/// <summary>
/// Ленивая очередь инициализации — разгружает холодный старт приложения.
/// Все три фазы выполняются строго в пуле потоков (Task.Run → Thread Pool):
/// UI-поток не блокируется ни на миллисекунду.
///
/// Защита от Race Condition:
/// Если пользователь взаимодействует с сервисом ДО того как StartupManager
/// запустил соответствующую фазу, _startSemaphore внутри сервиса гарантирует,
/// что параллельного дублирования инициализации не произойдёт.
/// Ожидающие вызовы могут использовать ISpeechTriggerService.WhenReadyAsync()
/// чтобы дождаться первого завершённого запуска (независимо от порядка).
/// </summary>
public sealed class StartupManager
{
    private const string Component = nameof(StartupManager);

    private readonly ILogService           _logger;
    private readonly ISpeechTriggerService _speech;
    private readonly IConfigService        _config;
    private readonly IProfileService       _profiles;

    public StartupManager(
        ILogService           logger,
        ISpeechTriggerService speech,
        IConfigService        config,
        IProfileService       profiles)
    {
        _logger   = logger;
        _speech   = speech;
        _config   = config;
        _profiles = profiles;
    }

    // Запускает фоновую очередь. Возвращает управление немедленно — Task не awaited намеренно.
    public void BeginLazyInitialization()
        => _ = Task.Run(RunInitQueueAsync);

    private async Task RunInitQueueAsync()
    {
        // ── Фаза 0: GPU-диагностика — запускается немедленно, до загрузки моделей ─
        await InitGpuDiagnosticsAsync().ConfigureAwait(false);

        // ── Фаза 1: SpeechTriggerService — 500 мс ────────────────────────────
        await Task.Delay(500).ConfigureAwait(false);
        await InitSpeechAsync().ConfigureAwait(false);

        // ── Фаза 2: NodeEngine — индексация графов макросов — 1500 мс ────────
        // Суммарная задержка: 500 + 1000 = 1500 мс от старта приложения
        await Task.Delay(1000).ConfigureAwait(false);
        await InitNodeGraphsAsync().ConfigureAwait(false);

        // ── Фаза 3: оставшиеся модули — 3000 мс ──────────────────────────────
        // Суммарная задержка: 1500 + 1500 = 3000 мс от старта приложения
        await Task.Delay(1500).ConfigureAwait(false);
        await InitRemainingAsync().ConfigureAwait(false);
    }

    // Фаза 0: GPU-самодиагностика.
    // Должна завершиться ДО InitSpeechAsync, чтобы WhisperModelWrapper и ModelManager
    // могли читать CudaDiagnostics.LastResult в своих лог-сообщениях.
    private async Task InitGpuDiagnosticsAsync()
    {
        try
        {
            await CudaDiagnostics.CheckCompatibilityAsync(_logger).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(Component,
                $"Startup: GPU-диагностика завершилась с ошибкой: {ex.Message}. " +
                "Продолжаем запуск.").ConfigureAwait(false);
        }
    }

    // Фаза 1: речевой движок.
    // InitializeAsync предзагружает модель Whisper ВСЕГДА, независимо от SpeechEnabled.
    // Это устраняет задержку при последующем включении голоса пользователем в настройках.
    // StartAsync/StartMonitoringAsync внутри используют _startSemaphore —
    // если пользователь кликнул "Включить микрофон" до этого вызова,
    // семафор заблокирует дублирование: этот вызов войдёт после завершения пользовательского.
    private async Task InitSpeechAsync()
    {
        try
        {
            // Предзагружаем модель Whisper безусловно — даже при SpeechEnabled=false.
            // Когда пользователь включит голос, модель уже будет в VRAM/RAM.
            await _speech.InitializeAsync().ConfigureAwait(false);

            // Запускаем захват аудио согласно конфигурации
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

    // Фаза 2: индексация профилей и макросов для NodeEngine.
    // В будущей версии: горячее восстановление последнего активного Blueprint из disk-state.
    private async Task InitNodeGraphsAsync()
    {
        try
        {
            var profileCount = _profiles.Profiles.Count;
            var macroCount   = _profiles.Profiles
                .Sum(p => p.Regions.Sum(r => r.Macros.Count));

            await _logger.LogInfoAsync(Component,
                $"Startup: NodeEngine инициализирован. " +
                $"Профилей: {profileCount}, макросов: {macroCount}.")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                "Startup: Ошибка индексации NodeEngine.", ex).ConfigureAwait(false);
        }
    }

    // Фаза 3: финальная готовность приложения.
    private async Task InitRemainingAsync()
    {
        try
        {
            await _logger.LogInfoAsync(Component,
                "Startup: Все фоновые модули инициализированы. Приложение полностью готово.")
                .ConfigureAwait(false);
        }
        catch { /* silent — не прерывать очередь из-за ошибки лога */ }
    }
}
