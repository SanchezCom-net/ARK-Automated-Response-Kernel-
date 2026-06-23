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
    private readonly IStorageManager       _storage;
    private readonly ISpeechTriggerService _speech;
    private readonly IEventMonitor         _eventMonitor;
    private readonly IProcessWatcher       _processWatcher;
    private readonly IHardwareAccelerator  _hardwareAccelerator;

    private volatile bool _isReady;

    public bool IsReady => _isReady;
    public event EventHandler? ReadyStateChanged;
    public event EventHandler<StartupPhaseEventArgs>? PhaseCompleted;

    public StartupOrchestrator(
        ILogService           logger,
        IConfigService        config,
        IStorageManager       storage,
        ISpeechTriggerService speech,
        IEventMonitor         eventMonitor,
        IProcessWatcher       processWatcher,
        IHardwareAccelerator  hardwareAccelerator)
    {
        _logger              = logger;
        _config              = config;
        _storage             = storage;
        _speech              = speech;
        _eventMonitor        = eventMonitor;
        _processWatcher      = processWatcher;
        _hardwareAccelerator = hardwareAccelerator;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await _logger.LogInfoAsync(Component,
                "=== Warm-up sequence: старт ===").ConfigureAwait(false);

            await RunPhaseAsync("GPU",          Phase_GpuAsync,          ct).ConfigureAwait(false);
            await Task.Delay(500, ct).ConfigureAwait(false);
            await RunPhaseAsync("MacroIndex",   Phase_MacroIndexAsync,   ct).ConfigureAwait(false);
            // EventMonitor строит кэш триггеров и подписывается на события ДО старта Speech
            await RunPhaseAsync("EventMonitor", Phase_EventMonitorAsync, ct).ConfigureAwait(false);
            await RunPhaseAsync("Speech",       Phase_SpeechAsync,       ct).ConfigureAwait(false);
            await Task.Delay(500, ct).ConfigureAwait(false);
            await RunPhaseAsync("Processes",    Phase_ProcessesAsync,    ct).ConfigureAwait(false);

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

    private async Task RunPhaseAsync(
        string name,
        Func<CancellationToken, Task> phase,
        CancellationToken ct)
    {
        try
        {
            await _logger.LogInfoAsync(Component, $"[{name}] Инициализация...").ConfigureAwait(false);
            await phase(ct).ConfigureAwait(false);
            await _logger.LogInfoAsync(Component, $"[{name}] Готово.").ConfigureAwait(false);
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
        await CudaDiagnostics.CheckCompatibilityAsync(_logger).ConfigureAwait(false);

        var cudaReady = await _hardwareAccelerator
            .WaitForCudaAsync(maxAttempts: 4, delayMilliseconds: 1000, ct)
            .ConfigureAwait(false);

        await _logger.LogInfoAsync(Component,
            $"[STARTUP] GPU-проверка завершена. CUDA: {cudaReady}. " +
            $"GPU: {_hardwareAccelerator.PrimaryGpuName ?? "не определён"}.")
            .ConfigureAwait(false);
    }

    private Task Phase_EventMonitorAsync(CancellationToken ct)
        // Подписки EventMonitor регистрируются в конструкторе — здесь только перестройка кэша.
        => _eventMonitor.RefreshTriggersCacheAsync(ct);

    private async Task Phase_SpeechAsync(CancellationToken ct)
    {
        await _speech.InitializeAsync().ConfigureAwait(false);
        if (_config.Current.SpeechEnabled)
            await _speech.StartAsync().ConfigureAwait(false);
        else
            await _speech.StartMonitoringAsync().ConfigureAwait(false);
    }

    private async Task Phase_MacroIndexAsync(CancellationToken ct)
    {
        await _storage.EnsureDirectoriesAsync(ct).ConfigureAwait(false);
        var macros = await _storage.GetAllMacrosAsync(ct).ConfigureAwait(false);
        _ = _logger.LogInfoAsync(Component,
            $"[MacroIndex] В хранилище {macros.Count} макрос(ов).");
    }

    private Task Phase_ProcessesAsync(CancellationToken ct)
    {
        _processWatcher.Start(ct);
        var count = _processWatcher.RunningProcessNames.Count;
        _ = _logger.LogInfoAsync(Component,
            $"[Processes] ProcessWatcher запущен. Активных процессов: {count}.");
        return Task.CompletedTask;
    }
}
