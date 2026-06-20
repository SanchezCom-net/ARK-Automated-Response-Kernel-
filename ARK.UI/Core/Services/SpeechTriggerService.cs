using System.Diagnostics;
using System.IO;
using System.Text;
using NAudio.Wave;
using ARK.UI.Core.Audio;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

public sealed class SpeechTriggerService : ISpeechTriggerService, IAsyncDisposable
{
    private const string Component     = "SpeechTriggerService";
    private const int    SampleRate    = 16_000;
    private const int    BitsPerSample = 16;
    // Минимум ~0.5 с аудио: 16000 Гц × 2 байта × 0.5 с = 16000 байт + WAV-заголовок (~44 байта)
    private const int    MinAudioBytes = 16_044;

    // Динамический Siri-style таймер тишины: короткие команды закрываются быстро,
    // длинные рассуждения получают право на естественные паузы внутри фразы
    private const int    ShortSilenceMs = 1200;   // буфер < 3.0 c
    private const int    LongSilenceMs  = 2500;   // буфер ≥ 3.0 c
    private const long   LongUtteranceThresholdBytes = SampleRate * 2 * 3;   // 3.0 c PCM16 = 96 000 байт

    // Канонические пути к моделям относительно директории исполняемого файла
    public static readonly string BaseModelPath =
        Path.Combine("Models", "Whisper", "base", "ggml-base.bin");
    public static readonly string TurboModelPath =
        Path.Combine("Models", "Whisper", "turbo", "ggml-large-v3-turbo.bin");

    private readonly ILogService               _logger;
    private readonly IConfigService            _configService;
    private readonly ISpeechSynthesisService   _ttsService;
    private readonly IModelManager             _modelManager;
    private readonly ITriggerService           _triggerService;

    private WaveInEvent? _waveIn;

    private readonly SemaphoreSlim _processingLock = new(1, 1);
    // Гарантирует, что StartAsync/StartMonitoringAsync не выполняются конкурентно.
    private readonly SemaphoreSlim _startSemaphore  = new(1, 1);
    // Сигнализирует ожидающим (WhenReadyAsync) что первый запуск завершён (успешно или нет).
    private readonly TaskCompletionSource _readyTcs  =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WaveFormat    _waveFormat      = new(SampleRate, BitsPerSample, 1);

    // Состояние захвата — доступ только под _captureLock
    private readonly object  _captureLock = new();
    private bool             _isCapturing;
    private bool             _inSilence;
    private bool             _longTimerLogged;
    private DateTime         _lastVoiceTime;
    private MemoryStream?    _audioBuffer;
    private WaveFileWriter?  _waveWriter;

    // RNNoise: создаётся при первом старте захвата, живёт до DisposeAsync
    private RnNoiseDenoiser? _denoiser;
    private bool             _denoiserLogged;

    private volatile bool _isRunning;
    private volatile bool _isMonitoring;

    public event Func<string, bool, Task>? SpeechRecognized;
    public event Action<double>?    LevelUpdated;
    public bool IsRunning    => _isRunning;
    public bool IsMonitoring => _isMonitoring;
    public Task WhenReadyAsync() => _readyTcs.Task;

    public SpeechTriggerService(
        ILogService             logger,
        IConfigService          configService,
        ISpeechSynthesisService ttsService,
        IModelManager           modelManager,
        ITriggerService         triggerService)
    {
        _logger         = logger;
        _configService  = configService;
        _ttsService     = ttsService;
        _modelManager   = modelManager;
        _triggerService = triggerService;
    }

    // ── Жизненный цикл ───────────────────────────────────────────────────────────

    /// <summary>
    /// Предзагружает модель (Whisper или Vosk) в память без запуска захвата аудио.
    /// Вызывается StartupManager при старте — делает последующий StartAsync() мгновенным.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_modelManager.IsReady) return;
        await _modelManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning) return;

        await _startSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isRunning) return; // Двойная проверка внутри семафора

            // Страховка: InitializeAsync мог не быть вызван до StartAsync
            if (!_modelManager.IsReady)
            {
                await _logger.LogWarningAsync(Component,
                    "[StartAsync] ModelManager не готов — запускаю InitializeAsync()...")
                    .ConfigureAwait(false);
                await _modelManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!_isMonitoring)
            {
                EnsureDenoiser();
                try
                {
                    _waveIn = new WaveInEvent
                    {
                        WaveFormat         = _waveFormat,
                        DeviceNumber       = _configService.Current.SpeechDeviceNumber,
                        BufferMilliseconds = 33
                    };
                    _waveIn.DataAvailable += OnDataAvailable;
                    _waveIn.StartRecording();
                    _isMonitoring = true;
                }
                catch (Exception ex)
                {
                    await _logger.LogErrorAsync(Component,
                        "Ошибка запуска WaveIn.", ex).ConfigureAwait(false);
                    return;
                }
            }

            _isMonitoring = false;   // Переходим в полный режим (VAD + транскрипция)
            _isRunning    = true;

            await _logger.LogInfoAsync(Component,
                $"Речевой триггер активирован. Движок: {_modelManager.ActiveModelType}.")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                "Ошибка активации речевого триггера.", ex).ConfigureAwait(false);
        }
        finally
        {
            _startSemaphore.Release();
            _readyTcs.TrySetResult();
        }
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;
        _isRunning    = false;
        _isMonitoring = false;
        await CleanupCaptureAsync().ConfigureAwait(false);
        await _logger.LogInfoAsync(Component, "Речевой триггер остановлен.").ConfigureAwait(false);
    }

    /// <summary>
    /// Переключает модель Whisper без перезапуска приложения (горячая смена).
    /// Делегирует в ModelManager: Dispose → GC → загрузка новой модели.
    /// </summary>
    public async Task SwitchModelAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        var wasRunning = _isRunning;
        if (wasRunning) await StopAsync().ConfigureAwait(false);

        // Ждём завершения активного инференса
        await _processingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _processingLock.Release();

        var language = _configService.Current.SpeechLanguage;
        await _modelManager.SwitchModelAsync(
            ModelType.Whisper, modelPath, language, cancellationToken)
            .ConfigureAwait(false);

        await _logger.LogInfoAsync(Component,
            $"Переключение модели Whisper → {Path.GetFileName(modelPath)}.")
            .ConfigureAwait(false);

        _configService.Current.WhisperModelPath = modelPath;

        if (wasRunning)
            await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Переключает движок (Auto/Whisper/Vosk) и язык без перезапуска приложения.
    /// Делегирует в ModelManager: Dispose → GC → загрузка нужной модели по пути Models/Vosk/model-{lang}.
    /// </summary>
    public async Task SwitchEngineAsync(
        SpeechEngineMode engine, string language,
        CancellationToken cancellationToken = default)
    {
        var wasRunning = _isRunning;
        if (wasRunning) await StopAsync().ConfigureAwait(false);

        // Ждём завершения активного инференса
        await _processingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _processingLock.Release();

        await _modelManager.SwitchEngineAsync(engine, language, cancellationToken)
            .ConfigureAwait(false);

        _configService.Current.SpeechLanguage       = language;
        _configService.Current.SelectedSpeechEngine = engine;

        await _logger.LogInfoAsync(Component,
            $"Движок переключён: {engine} / Язык: {language}. Активная модель: {_modelManager.ActiveModelType}.")
            .ConfigureAwait(false);

        if (wasRunning)
            await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── Режим мониторинга (WaveIn без инференса — только VU-Meter) ──────────────

    public async Task StartMonitoringAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning || _isMonitoring) return;

        await _startSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isRunning || _isMonitoring) return;

            EnsureDenoiser();
            try
            {
                _waveIn = new WaveInEvent
                {
                    WaveFormat         = _waveFormat,
                    DeviceNumber       = _configService.Current.SpeechDeviceNumber,
                    BufferMilliseconds = 33
                };
                _waveIn.DataAvailable += OnDataAvailable;
                _waveIn.StartRecording();
                _isMonitoring = true;
                await _logger.LogInfoAsync(Component,
                    "Мониторинг микрофона активирован (VU-Meter).").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _logger.LogErrorAsync(Component,
                    "Ошибка запуска мониторинга микрофона.", ex).ConfigureAwait(false);
                await CleanupCaptureAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _startSemaphore.Release();
            _readyTcs.TrySetResult();
        }
    }

    public async Task StopMonitoringAsync()
    {
        if (!_isMonitoring) return;
        _isMonitoring = false;
        await CleanupCaptureAsync().ConfigureAwait(false);
        await _logger.LogInfoAsync(Component, "Мониторинг микрофона остановлен.").ConfigureAwait(false);
    }

    // ── Захват аудио (NAudio DataAvailable) ──────────────────────────────────────

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        // Подавление эха: ассистент говорит → обнуляем буфер → VU-Meter и VAD видят тишину
        if (_ttsService.IsSpeaking)
            Array.Clear(e.Buffer, 0, e.BytesRecorded);
        else
            // RNNoise: чистим чанк от шума кулеров/клавиатуры/фона СТРОГО ДО расчёта RMS
            _denoiser?.ProcessInPlace(e.Buffer, e.BytesRecorded);

        var rms = CalculateRms(e.Buffer, e.BytesRecorded);
        LevelUpdated?.Invoke(rms);   // Всегда: и в мониторинге, и в полном режиме

        if (!_isRunning) return;     // Режим мониторинга: только VU-Meter, VAD пропускаем

        var threshold = _configService.Current.SpeechRmsThreshold;

        lock (_captureLock)
        {
            if (rms >= threshold)
            {
                if (!_isCapturing)
                {
                    _isCapturing     = true;
                    _inSilence       = false;
                    _longTimerLogged = false;
                    _audioBuffer     = new MemoryStream();
                    _waveWriter      = new WaveFileWriter(_audioBuffer, _waveFormat);
                    _ = _logger.LogInfoAsync(Component,
                        $"[VAD] Обнаружен старт речи (RMS: {rms:F4} > Порог: {threshold:F4}). Начинаю запись буфера.");
                }
                else if (_inSilence)
                {
                    _inSilence = false;
                    _ = _logger.LogInfoAsync(Component,
                        $"[VAD] Обнаружено продолжение речи (RMS: {rms:F4}). Сброс таймера тишины.");
                }

                _lastVoiceTime = DateTime.UtcNow;
                _waveWriter!.Write(e.Buffer, 0, e.BytesRecorded);
                LogLongUtteranceTransitionLocked();
            }
            else if (_isCapturing)
            {
                // Дописываем тишину — плавное завершение фразы для Whisper/Vosk
                _waveWriter!.Write(e.Buffer, 0, e.BytesRecorded);
                LogLongUtteranceTransitionLocked();

                var currentTimeout = CurrentSilenceTimeoutLocked();
                if (!_inSilence)
                {
                    _inSilence = true;
                    _ = _logger.LogInfoAsync(Component,
                        $"[VAD] Пользователь замолчал. Запуск таймера тишины ({currentTimeout} мс)...");
                }

                if ((DateTime.UtcNow - _lastVoiceTime).TotalMilliseconds >= currentTimeout)
                    FlushAndScheduleTranscription();
            }
        }
    }

    // Динамический Siri-style порог тишины. Вызывается строго под _captureLock.
    private int CurrentSilenceTimeoutLocked()
        => _waveWriter is not null && _waveWriter.Length >= LongUtteranceThresholdBytes
            ? LongSilenceMs
            : ShortSilenceMs;

    // Однократный лог перехода записи через границу 3.0 с. Вызывается строго под _captureLock.
    private void LogLongUtteranceTransitionLocked()
    {
        if (_longTimerLogged
            || _waveWriter is null
            || _waveWriter.Length < LongUtteranceThresholdBytes) return;

        _longTimerLogged = true;
        _ = _logger.LogInfoAsync(Component,
            $"[VAD] Длина записи превысила 3.0 сек. Siri-style таймер увеличен до {LongSilenceMs} мс.");
    }

    // Вызывается под _captureLock: атомарно забирает буфер и планирует транскрипцию
    private void FlushAndScheduleTranscription()
    {
        _isCapturing = false;
        _inSilence   = false;

        var writer = _waveWriter;
        var buffer = _audioBuffer;
        _waveWriter  = null;
        _audioBuffer = null;

        if (writer is null || buffer is null) return;

        var audioDuration = writer.Length / (double)(SampleRate * 2);
        _ = _logger.LogInfoAsync(Component,
            $"[VAD] Таймер тишины истёк. Запись завершена. Длина: {audioDuration:F1} сек. " +
            $"Отправка в {_modelManager.ActiveModelType}.");

        // Dispose финализирует RIFF-заголовок WAV в MemoryStream
        writer.Dispose();

        // Изолированная копия: TranscribeAsync владеет своим потоком и закрывает его сам
        var snapshot = new MemoryStream(buffer.ToArray(), writable: false);
        buffer.Dispose();

        _ = Task.Run(() => TranscribeAsync(snapshot));
    }

    // ── Инференс (отдельный Task.Run, не блокирует WPF / хуки) ──────────────────

    private async Task TranscribeAsync(MemoryStream audioBuffer)
    {
        if (!_modelManager.IsReady) { audioBuffer.Dispose(); return; }

        await _processingLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (audioBuffer.Length < MinAudioBytes)
            {
                await _logger.LogInfoAsync(Component,
                    "Аудиофрагмент слишком короткий — пропуск.").ConfigureAwait(false);
                return;
            }

            audioBuffer.Position = 0;

            var sw   = Stopwatch.StartNew();
            var text = await _modelManager.RecognizeAsync(audioBuffer).ConfigureAwait(false);
            sw.Stop();

            text = text.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            await _logger.LogInfoAsync(Component,
                $"[ГОЛОС] Распознано: '{text}' " +
                $"(инференс: {sw.Elapsed.TotalMilliseconds:F0} мс, модель: {_modelManager.ActiveModelType})")
                .ConfigureAwait(false);

            // Фильтр активации: публикуем событие только если система перешла или находится в Active.
            // В Idle-состоянии текст без ключевого слова игнорируется (команды не выполняются).
            if (!_triggerService.Evaluate(text)) return;

            // Жёсткая блокировка макросов: если гейткипер отключён пользователем —
            // события для MacroScheduler не генерируются; звуковой поток отдаётся ИИ-ассистенту.
            if (!_configService.Current.UseWakeWordGatekeeper)
            {
                await _logger.LogInfoAsync(Component,
                    "[SpeechTriggerService] Гейткипер имени отключен. Выполнение голосовых макросов заблокировано (ожидание ИИ-ассистента).")
                    .ConfigureAwait(false);
                return;
            }

            // Wake-word гейткипер: проверяем, начинается ли фраза с имени активации.
            // Если ActivationNames пустое — гейткипер отключён, вся фраза передаётся как есть.
            var (gatedText, activationNameDetected) = ApplyActivationGatekeeper(text);
            if (gatedText is null)
            {
                await _logger.LogInfoAsync(Component,
                    $"[ГЕЙТКИПЕР] Фраза отклонена: не начинается с имени активации. «{text}»")
                    .ConfigureAwait(false);
                return;
            }

            if (activationNameDetected)
                await _logger.LogInfoAsync(Component,
                    $"[ГЕЙТКИПЕР] Активация по имени подтверждена. Полезная нагрузка: «{gatedText}»")
                    .ConfigureAwait(false);

            if (SpeechRecognized is { } handler)
                await handler(gatedText, activationNameDetected).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                "Ошибка инференса.", ex).ConfigureAwait(false);
        }
        finally
        {
            _processingLock.Release();
            audioBuffer.Dispose();
        }
    }

    // ── RNNoise (нейросетевой шумодав) ───────────────────────────────────────────

    private void EnsureDenoiser()
    {
        if (_denoiser is not null) return;
        _denoiser = new RnNoiseDenoiser();

        if (_denoiserLogged) return;
        _denoiserLogged = true;

        if (_denoiser.IsAvailable)
        {
            _ = _logger.LogInfoAsync(nameof(SpeechTriggerService),
                "[RNNoise] Шумоподавление успешно активировано в аудио-тракте (16kHz Mono).");
            return;
        }

        var reason = _denoiser.InitializationError switch
        {
            DllNotFoundException        => "rnnoise.dll не найдена в директории приложения и путях поиска DLL (0x8007007E)",
            BadImageFormatException     => "rnnoise.dll имеет неверную разрядность — требуется x64-сборка",
            EntryPointNotFoundException => "в rnnoise.dll отсутствуют ожидаемые экспорты (rnnoise_create / rnnoise_process_frame)",
            System.Runtime.InteropServices.MarshalDirectiveException
                                        => "ошибка маршаллинга при привязке нативных функций rnnoise",
            InvalidOperationException   => "rnnoise_create вернул NULL — нативное выделение памяти не удалось",
            { } ex                      => ex.Message,
            null                        => "неизвестная причина"
        };

        _ = _logger.LogWarningAsync(nameof(SpeechTriggerService),
            $"[RNNoise] Шумоподавление временно неактивно (режим Bypass). Причина: {reason}. Для запуска:");
        _ = _logger.LogWarningAsync(nameof(SpeechTriggerService),
            "1. Скачайте x64 dll из релизов: https://github.com/werman/noise-suppression-for-voice/releases");
        _ = _logger.LogWarningAsync(nameof(SpeechTriggerService),
            "2. Поместите файл rnnoise.dll в папку Tools/RNNoise/ или прямо в корень программы.");
        _ = _logger.LogWarningAsync(nameof(SpeechTriggerService),
            "3. Если ошибка осталась, установите VC++ Runtime: https://aka.ms/vs/17/release/vc_redist.x64.exe");
    }

    // ── Очистка ресурсов ─────────────────────────────────────────────────────

    private Task CleanupCaptureAsync()
    {
        lock (_captureLock)
        {
            _isCapturing = false;
            _waveWriter?.Dispose();
            _waveWriter  = null;
            _audioBuffer?.Dispose();
            _audioBuffer = null;
        }

        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            try { _waveIn.StopRecording(); } catch { /* устройство уже остановлено */ }
            _waveIn.Dispose();
            _waveIn = null;
        }

        return Task.CompletedTask;
    }

    // ── Wake-word гейткипер ──────────────────────────────────────────────────

    // Возвращает (Text, WasGated).
    // Text=null — фраза заблокирована (имена заданы, но ни одно не совпало с началом).
    // WasGated=true — имя активации найдено и отсечено, Text содержит очищенный хвост.
    // WasGated=false + Text=исходный — гейткипер отключён (ActivationNames пустое).
    private (string? Text, bool WasGated) ApplyActivationGatekeeper(string text)
    {
        var raw = _configService.Current.ActivationNames;
        if (string.IsNullOrWhiteSpace(raw)) return (text, false); // Гейткипер отключён

        var names = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Take(3)
                       .Select(n => n.ToLowerInvariant())
                       .Where(n => n.Length > 0)
                       .ToArray();

        if (names.Length == 0) return (text, false);

        var lowerText = text.ToLowerInvariant();
        foreach (var name in names)
        {
            if (!lowerText.StartsWith(name, StringComparison.Ordinal)) continue;

            // Отсекаем имя активации; чистим разделители (запятая, пробел, пунктуация) спереди
            var tail = text[name.Length..].TrimStart(' ', ',', '.', '!', '?', ';', ':');
            return tail.Length > 0 ? (tail, true) : (null, false);
        }

        return (null, false);
    }

    // RMS для 16-bit PCM little-endian, нормализованный в [0..1]
    private static float CalculateRms(byte[] buffer, int count)
    {
        var sampleCount = count / 2;
        if (sampleCount == 0) return 0f;

        var sum = 0f;
        for (var i = 0; i < count - 1; i += 2)
        {
            var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            var norm   = sample / 32768f;
            sum += norm * norm;
        }
        return MathF.Sqrt(sum / sampleCount);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isRunning)
            await StopAsync().ConfigureAwait(false);
        else if (_isMonitoring)
            await StopMonitoringAsync().ConfigureAwait(false);

        // Ждём завершения активного инференса перед освобождением ресурсов
        await _processingLock.WaitAsync().ConfigureAwait(false);
        try { }
        finally
        {
            _processingLock.Release();
            _processingLock.Dispose();
            _startSemaphore.Dispose();
            _readyTcs.TrySetResult();
            // ModelManager освобождается DI-контейнером (зарегистрирован до SpeechTriggerService)
        }

        _denoiser?.Dispose();
        _denoiser = null;
    }
}
