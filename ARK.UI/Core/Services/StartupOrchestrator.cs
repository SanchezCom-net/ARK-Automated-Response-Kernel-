using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Services;

/// <summary>
/// Последовательный прогрев подсистем ARK.
/// Все фазы выполняются строго в фоновом потоке — UI-поток не блокируется.
/// Ошибка в одной фазе не прерывает последующие (кроме OperationCanceledException).
/// </summary>
public sealed class StartupOrchestrator : IStartupOrchestrator
{
    private const string Component = nameof(StartupOrchestrator);

    private readonly ILogService           _logger;
    private readonly IConfigService        _config;
    private readonly IProfileService       _profiles;
    private readonly ISpeechTriggerService _speech;
    private readonly IProcessWatcher       _processWatcher;
    private readonly IHardwareAccelerator  _hardwareAccelerator;

    private volatile bool _isReady;

    public bool IsReady => _isReady;
    public event EventHandler? ReadyStateChanged;
    public event EventHandler<StartupPhaseEventArgs>? PhaseCompleted;

    public StartupOrchestrator(
        ILogService           logger,
        IConfigService        config,
        IProfileService       profiles,
        ISpeechTriggerService speech,
        IProcessWatcher       processWatcher,
        IHardwareAccelerator  hardwareAccelerator)
    {
        _logger              = logger;
        _config              = config;
        _profiles            = profiles;
        _speech              = speech;
        _processWatcher      = processWatcher;
        _hardwareAccelerator = hardwareAccelerator;
    }

    // Вызывается из App.xaml.cs через Task.Run — не трогает UI-поток.
    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await _logger.LogInfoAsync(Component,
                "=== Warm-up sequence: старт ===").ConfigureAwait(false);

            // ── Фаза 0: GPU-диагностика ──────────────────────────────────────────
            await RunPhaseAsync("GPU", Phase_GpuAsync, ct).ConfigureAwait(false);

            // ── Фаза 1: Речевые модели — 500 мс после старта ─────────────────────
            await Task.Delay(500, ct).ConfigureAwait(false);
            await RunPhaseAsync("Speech", Phase_SpeechAsync, ct).ConfigureAwait(false);

            // ── Фаза 2: Индексация макросов — 1000 мс после речи ─────────────────
            await Task.Delay(1000, ct).ConfigureAwait(false);
            await RunPhaseAsync("MacroIndex", Phase_MacroIndexAsync, ct).ConfigureAwait(false);

            // ── Фаза 3: Сканирование процессов ОС — сразу после индексации ────────
            await RunPhaseAsync("Processes", Phase_ProcessesAsync, ct).ConfigureAwait(false);

            // ── Финал: IsReady = true ─────────────────────────────────────────────
            await Task.Delay(250, ct).ConfigureAwait(false);
            _isReady = true;
            ReadyStateChanged?.Invoke(this, EventArgs.Empty);
            PhaseCompleted?.Invoke(this, new StartupPhaseEventArgs("Ready", true));

            await _logger.LogInfoAsync(Component,
                "=== Все подсистемы инициализированы. IsReady = true ===")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _logger.LogInfoAsync(Component,
                "Warm-up sequence отменена.").ConfigureAwait(false);
        }
    }

    // Выполняет одну фазу с логированием. Ошибка в фазе не роняет очередь.
    private async Task RunPhaseAsync(
        string name,
        Func<CancellationToken, Task> phase,
        CancellationToken ct)
    {
        try
        {
            await _logger.LogInfoAsync(Component,
                $"[{name}] Инициализация...").ConfigureAwait(false);
            await phase(ct).ConfigureAwait(false);
            await _logger.LogInfoAsync(Component,
                $"[{name}] Готово.").ConfigureAwait(false);
            PhaseCompleted?.Invoke(this, new StartupPhaseEventArgs(name, true));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                $"[{name}] Ошибка при инициализации фазы.", ex).ConfigureAwait(false);
            PhaseCompleted?.Invoke(this, new StartupPhaseEventArgs(name, false, ex.Message));
        }
    }

    private async Task Phase_GpuAsync(CancellationToken ct)
    {
        // Шаг 1: высокоуровневый аудит (nvidia-smi, cudart64 версии, инструкция по установке)
        await CudaDiagnostics.CheckCompatibilityAsync(_logger).ConfigureAwait(false);

        // Шаг 2: адаптивный проб готовности DLL
        // При холодном старте ОС драйвер CUDA может инициализироваться с задержкой до нескольких секунд.
        // Если DLL доступна сразу — fast path, без ожиданий. Иначе — до 4 попыток × 1 сек.
        var cudaReady = await _hardwareAccelerator
            .WaitForCudaAsync(maxAttempts: 4, delayMilliseconds: 1000, ct)
            .ConfigureAwait(false);

        await _logger.LogInfoAsync(Component,
            $"[STARTUP] Адаптивная проверка GPU завершена. Результат CUDA: {cudaReady}. " +
            $"GPU: {_hardwareAccelerator.PrimaryGpuName ?? "не определён"}.")
            .ConfigureAwait(false);
    }

    private async Task Phase_SpeechAsync(CancellationToken ct)
    {
        await _speech.InitializeAsync().ConfigureAwait(false);
        if (_config.Current.SpeechEnabled)
            await _speech.StartAsync().ConfigureAwait(false);
        else
            await _speech.StartMonitoringAsync().ConfigureAwait(false);
    }

    private Task Phase_MacroIndexAsync(CancellationToken ct)
    {
        var profileCount = _profiles.Profiles.Count;
        _ = _logger.LogInfoAsync(Component,
            $"[MacroIndex] {profileCount} профилей в памяти.");
        return Task.CompletedTask;
    }

    private Task Phase_ProcessesAsync(CancellationToken ct)
    {
        _processWatcher.Start(ct);
        var count = _processWatcher.RunningProcessNames.Count;
        _ = _logger.LogInfoAsync(Component,
            $"[Processes] ProcessWatcher запущен. Активных процессов в ОС: {count}.");
        return Task.CompletedTask;
    }
}
