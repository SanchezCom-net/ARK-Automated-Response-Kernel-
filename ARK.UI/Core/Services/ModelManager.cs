using System.IO;
using System.Threading;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

public sealed class ModelManager : IModelManager
{
    private const string Component = "ModelManager";

    private readonly ILogService            _logger;
    private readonly IConfigService         _configService;
    private readonly IOverlayService        _overlayService;
    private readonly IHardwareAccelerator   _hardware;

    private IModelWrapper?          _activeWrapper;
    private System.Action?          _whisperFaultedHandler;
    private readonly SemaphoreSlim  _switchLock =
        new(1, 1);
    private readonly TaskCompletionSource _readyTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private System.Threading.Timer? _watchdogTimer;
    private string          _currentLanguage = string.Empty;
    private SpeechEngineMode _currentEngine  = SpeechEngineMode.Auto;
    private bool             _disposed;

    public ModelType ActiveModelType => _activeWrapper?.Type ?? ModelType.None;
    public bool      IsReady         => _activeWrapper?.IsReady ?? false;

    public ModelManager(
        ILogService logger, IConfigService configService,
        IOverlayService overlayService, IHardwareAccelerator hardware)
    {
        _logger         = logger;
        _configService  = configService;
        _overlayService = overlayService;
        _hardware       = hardware;
        _configService.ConfigSaved += OnConfigSaved;
    }

    // ── Инициализация ─────────────────────────────────────────────────────────

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_disposed) return;

        await _switchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_activeWrapper?.IsReady == true) return;

            _currentLanguage = _configService.Current.SpeechLanguage;
            _currentEngine   = _configService.Current.SelectedSpeechEngine;
            bool gpuRequested = _configService.Current.UseGpuAcceleration;

            await LoadByEngineLockedAsync(_currentEngine, _currentLanguage, gpuRequested, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _switchLock.Release();
            _readyTcs.TrySetResult();
        }
    }

    // ── Смена движка (публичный API) ──────────────────────────────────────────

    public async Task SwitchEngineAsync(
        SpeechEngineMode engine, string language,
        CancellationToken ct = default)
    {
        if (_disposed) return;

        await _logger.LogInfoAsync(Component,
            $"[ModelManager] Смена движка: {_currentEngine} → {engine}. Язык: {language}.")
            .ConfigureAwait(false);

        await _switchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return;

            await DisposeActiveWrapperLockedAsync().ConfigureAwait(false);
            _currentLanguage = language;
            _currentEngine   = engine;

            bool gpuRequested = _configService.Current.UseGpuAcceleration;
            await LoadByEngineLockedAsync(engine, language, gpuRequested, ct).ConfigureAwait(false);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    // ── Смена модели по типу (устаревший путь, используется SpeechTriggerService) ─

    public async Task SwitchModelAsync(
        ModelType type, string modelPath, string language,
        CancellationToken ct = default)
    {
        if (_disposed) return;

        await _logger.LogInfoAsync(Component,
            $"[ModelManager] Переключение: {ActiveModelType} → {type}. Язык: {language}.")
            .ConfigureAwait(false);

        await _switchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return;

            await DisposeActiveWrapperLockedAsync().ConfigureAwait(false);
            _currentLanguage = language;

            if (type == ModelType.Whisper)
            {
                bool forceGpu = _configService.AppSettings.GpuSettings.ForceGpuInitialization;
                await LoadWhisperLockedAsync(language, forceGpu, ct).ConfigureAwait(false);
            }
            else if (type == ModelType.Vosk)
                await LoadVoskLockedAsync(language, ct).ConfigureAwait(false);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    // ── Распознавание ──────────────────────────────────────────────────────────

    public Task<string> RecognizeAsync(System.IO.Stream audioWav, CancellationToken ct = default)
    {
        if (!IsReady) return Task.FromResult(string.Empty);
        return _activeWrapper!.RecognizeAsync(audioWav, ct);
    }

    public Task WhenReadyAsync() => _readyTcs.Task;

    // ── Внутренняя логика выбора движка (вызывается строго под _switchLock) ──

    private async Task LoadByEngineLockedAsync(
        SpeechEngineMode engine, string language, bool gpuRequested,
        CancellationToken ct)
    {
        // Останавливаем Watchdog перед любой сменой — он будет перезапущен при необходимости
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;

        bool forceGpu = _configService.AppSettings.GpuSettings.ForceGpuInitialization;

        switch (engine)
        {
            case SpeechEngineMode.Whisper:
                if (gpuRequested && !_hardware.IsGpuAccelerationAvailable)
                {
                    if (forceGpu)
                    {
                        await _logger.LogWarningAsync(Component,
                            "[ModelManager] ForceGpuInitialization=true: GPU диагностика не нашла ускорителя, " +
                            "но принудительный режим включён. WhisperHost запускается с UseGpu=true. " +
                            "При реальном отсутствии CUDA придёт HALT-сигнал и произойдёт переключение на Vosk.")
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        // Ручной выбор Whisper с GPU и GPU недоступен — НЕ переходим на CPU тихо.
                        // Бросаем детальное исключение: пусть лучше упадёт с понятным сообщением.
                        var wProbe  = CudaDiagnostics.LastResult;
                        var details = BuildAcceleratorDetails() + BuildCudaContext(wProbe);
                        var errMsg  =
                            $"[ModelManager] Whisper (ручной режим+GPU): GPU запрошен, но недоступен. " +
                            $"{details}" +
                            $"Убедитесь что ggml-cuda-whisper.dll присутствует в папке ARK " +
                            $"({AppContext.BaseDirectory}). " +
                            $"Или включите GpuSettings.ForceGpuInitialization=true в appsettings.json " +
                            $"чтобы принудительно передать UseGpu=true в WhisperHost.";

                        await _logger.LogErrorAsync(Component, errMsg).ConfigureAwait(false);
                        _ = _overlayService.ShowTextAsync(
                            "❌ GPU недоступен — проверьте ggml-cuda-whisper.dll и перезапустите ARK", 10_000);
                        throw new InvalidOperationException(errMsg);
                    }
                }
                await LoadWhisperLockedAsync(language, forceGpu, ct).ConfigureAwait(false);
                break;

            case SpeechEngineMode.Vosk:
                await LoadVoskLockedAsync(language, ct).ConfigureAwait(false);
                break;

            default: // Auto
                if (forceGpu)
                {
                    await _logger.LogWarningAsync(Component,
                        "[ModelManager] ForceGpuInitialization=true (Auto): принудительный запуск " +
                        "WhisperHost с UseGpu=true, минуя GPU-диагностику. " +
                        "Если CUDA отсутствует — WhisperHost завершится с HALT-сигналом и " +
                        "автоматически произойдёт переключение на Vosk.")
                        .ConfigureAwait(false);
                    await LoadWhisperLockedAsync(language, forceGpu: true, ct).ConfigureAwait(false);
                }
                else if (!gpuRequested || _hardware.IsGpuAccelerationAvailable)
                {
                    await LoadWhisperLockedAsync(language, forceGpu: false, ct).ConfigureAwait(false);
                }
                else
                {
                    var aProbe = CudaDiagnostics.LastResult;
                    await _logger.LogWarningAsync(Component,
                        "[ModelManager] Auto: GPU-ускорение недоступно. " +
                        BuildAcceleratorDetails() +
                        BuildCudaContext(aProbe) +
                        " Переключаюсь на Vosk (CPU fallback). Watchdog проверяет GPU каждые 30 сек.")
                        .ConfigureAwait(false);
                    await LoadVoskLockedAsync(language, ct).ConfigureAwait(false);
                    StartGpuWatchdog();
                }
                break;
        }
    }

    // ── Загрузчики (вызываются строго под _switchLock) ────────────────────────

    private async Task LoadWhisperLockedAsync(string language, bool forceGpu, CancellationToken ct)
    {
        var raw       = _configService.Current.WhisperModelPath;
        var modelPath = Path.GetFullPath(string.IsNullOrWhiteSpace(raw)
            ? Path.Combine("Models", "Whisper", "base", "ggml-base.bin") : raw);

        // forceGpu=true: разрешаем UseGpu даже если IsCudaAvailable=false — WhisperHost сам
        // обнаружит GPU и, при неудаче, вернёт HALT-сигнал (нет тихого CPU-фолбэка).
        bool useGpu = _configService.Current.UseGpuAcceleration &&
                      (_hardware.IsCudaAvailable || forceGpu);

        if (forceGpu && !_hardware.IsCudaAvailable && useGpu)
        {
            await _logger.LogInfoAsync(Component,
                "[ModelManager] ForceGpuInitialization: IsCudaAvailable=false, " +
                "но UseGpu=true передан в WhisperHost. WhisperHost проведёт собственную проверку GPU.")
                .ConfigureAwait(false);
        }

        var gpuDelayMs = _configService.AppSettings.GpuSettings.GpuStartupDelayMs;
        var wrapper = new ExternalWhisperService(
            _logger, _overlayService, _configService.AppSettings.WhisperSettings,
            useGpu, gpuStartupDelayMs: gpuDelayMs);

        // Подписываемся на событие Faulted для авто-переключения на Vosk
        _whisperFaultedHandler = OnWhisperFaulted;
        wrapper.Faulted += _whisperFaultedHandler;

        await wrapper.InitializeAsync(modelPath, language, ct).ConfigureAwait(false);
        _activeWrapper = wrapper;

        await _logger.LogInfoAsync(Component,
            $"[ModelManager] WhisperHost запущен. Модель: {Path.GetFileName(modelPath)}, " +
            $"GPU: {useGpu} (ForceGpu: {forceGpu}), язык: {language}.")
            .ConfigureAwait(false);
    }

    // Вызывается когда WhisperHost исчерпал лимит перезапусков — авто-переключение на Vosk
    private void OnWhisperFaulted()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _logger.LogErrorAsync(Component,
                    "[Watchdog] WhisperHost упал и не смог перезапуститься. " +
                    "Автопереключение на Vosk (CPU fallback)...")
                    .ConfigureAwait(false);
                await SwitchEngineAsync(SpeechEngineMode.Vosk, _currentLanguage)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _logger.LogErrorAsync(Component,
                    "[Watchdog] Не удалось переключиться на Vosk после сбоя Whisper.", ex)
                    .ConfigureAwait(false);
            }
        });
    }

    private async Task LoadVoskLockedAsync(string language, CancellationToken ct)
    {
        // Путь из VoskSettings.ModelBasePath — единый источник правды (appsettings.json).
        // ConfigService гарантирует непустое значение: авто-заполняет BaseDirectory/Models/Vosk при загрузке.
        var modelPath = Path.Combine(
            _configService.AppSettings.VoskSettings.ModelBasePath,
            $"model-{language.ToLowerInvariant()}");

        await _logger.LogInfoAsync(Component,
            $"[ModelManager] Vosk→VoskHost. Путь модели: '{modelPath}'.")
            .ConfigureAwait(false);

        // VoskModelWrapper заменён на ExternalVoskService (изолированный процесс VoskHost.exe).
        // Весь код Vosk изолирован в ARK.VoskHost — ARK.UI не имеет прямой зависимости от Vosk SDK.
        var wrapper = new ExternalVoskService(_logger, _overlayService, _configService.AppSettings.VoskSettings);
        await wrapper.InitializeAsync(modelPath, language, ct).ConfigureAwait(false);
        _activeWrapper = wrapper;

        await _logger.LogInfoAsync(Component,
            $"[ModelManager] VoskHost запущен (model-{language.ToLowerInvariant()}, язык: {language}).")
            .ConfigureAwait(false);
    }

    private async Task DisposeActiveWrapperLockedAsync()
    {
        if (_activeWrapper is null) return;

        // Отписываемся от Faulted до Dispose, чтобы обработчик не сработал после смены модели
        if (_activeWrapper is ExternalWhisperService ws && _whisperFaultedHandler is not null)
        {
            ws.Faulted -= _whisperFaultedHandler;
            _whisperFaultedHandler = null;
        }

        var old = _activeWrapper;
        _activeWrapper = null;

        await old.DisposeAsync().ConfigureAwait(false);

        // Принудительный GC: освобождаем VRAM/RAM перед загрузкой новой модели,
        // чтобы нативные финализаторы Whisper/Vosk успели вернуть ресурсы системе.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();

        await _logger.LogInfoAsync(Component,
            "[ModelManager] Старая модель выгружена, VRAM/RAM освобождена.")
            .ConfigureAwait(false);
    }

    // Форматирует текущее состояние IHardwareAccelerator по каждому бэкенду.
    private string BuildAcceleratorDetails()
        => $"[CUDA={_hardware.IsCudaAvailable}, DirectML={_hardware.IsDirectMlAvailable}, " +
           $"ROCm={_hardware.IsRocmAvailable}, GPU={_hardware.PrimaryGpuName ?? "N/A"}] ";

    // Форматирует CUDA-контекст для вставки в лог-сообщения.
    private static string BuildCudaContext(CudaProbeResult? probe)
    {
        if (probe is null || !probe.GpuFound) return string.Empty;
        var sys = probe.SystemCudaVersion;
        var req = probe.BundledCudaVersion is not null
            ? $"CUDA {probe.BundledCudaVersion}.x"
            : "CUDA 12.x";
        return $"CudaDiag: система CUDA {sys} vs пакет {req}. ";
    }

    // ── GPU Watchdog (только для Auto-режима без CUDA при старте) ────────────

    private void StartGpuWatchdog()
    {
        _watchdogTimer?.Dispose();
        _watchdogTimer = new System.Threading.Timer(
            WatchdogTick, null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    private void WatchdogTick(object? _)
        => _ = Task.Run(CheckAndUpgradeToWhisperAsync);

    private async Task CheckAndUpgradeToWhisperAsync()
    {
        if (_disposed || _activeWrapper?.Type != ModelType.Vosk) return;
        await _hardware.RefreshAsync().ConfigureAwait(false);
        if (!_hardware.IsGpuAccelerationAvailable) return;

        await _logger.LogInfoAsync(Component,
            "[Watchdog] GPU появился в системе! Переключение Vosk → Whisper (CUDA)...")
            .ConfigureAwait(false);

        try
        {
            _watchdogTimer?.Dispose();
            _watchdogTimer = null;

            var language = _configService.Current.SpeechLanguage;
            await SwitchEngineAsync(SpeechEngineMode.Auto, language).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                "[Watchdog] Ошибка при переключении Vosk → Whisper.", ex)
                .ConfigureAwait(false);
        }
    }

    // ── Авто-триггер при смене конфига ────────────────────────────────────────

    private void OnConfigSaved()
    {
        var newLang   = _configService.Current.SpeechLanguage;
        var newEngine = _configService.Current.SelectedSpeechEngine;

        var langChanged   = !string.IsNullOrEmpty(newLang) && newLang != _currentLanguage;
        var engineChanged = newEngine != _currentEngine;

        if (!langChanged && !engineChanged) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _logger.LogInfoAsync(Component,
                    $"[ModelManager] Конфиг изменён — движок: {newEngine}, язык: {newLang}. Перезагрузка...")
                    .ConfigureAwait(false);
                await SwitchEngineAsync(newEngine, newLang).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _logger.LogErrorAsync(Component,
                    "[ModelManager] Ошибка перезагрузки при смене конфига.", ex)
                    .ConfigureAwait(false);
            }
        });
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _configService.ConfigSaved -= OnConfigSaved;

        _watchdogTimer?.Dispose();
        _watchdogTimer = null;

        await _switchLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeWrapper is not null)
            {
                await _activeWrapper.DisposeAsync().ConfigureAwait(false);
                _activeWrapper = null;
            }
        }
        finally
        {
            _switchLock.Release();
        }

        _readyTcs.TrySetResult();
    }
}
