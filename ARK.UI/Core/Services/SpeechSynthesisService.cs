using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Services;

public sealed class SpeechSynthesisService : ISpeechSynthesisService
{
    // Демпфирование: окно ожидания следующего предложения перед полной остановкой
    // WaveOutEvent. Sentence-Level TTS читает фразы из канала с паузами генерации Ollama —
    // мгновенный teardown устройства давал постоянные реинициализации ("TTS Init → WaveOut: Stopped").
    private const int IdleGraceMs = 500;

    private static readonly string KokoroModelPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "TTS", "Kokoro", "kokoro-v1.0.onnx");

    private readonly ILogService   _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Process?                 _currentProcess;
    private WaveOutEvent?            _currentWaveOut;
    private CancellationTokenSource? _speakCts;
    private volatile bool            _warmupDone;
    private KokoroTTS?               _kokoroEngine;

    // Переиспользуемое аудиоустройство — живёт между предложениями (тёплый WaveOut).
    // Доступ к полям строго под _deviceLock.
    private readonly object          _deviceLock = new();
    private WaveOutEvent?            _sharedWaveOut;
    private BufferedWaveProvider?    _sharedProvider;
    private int                      _sharedSampleRate;
    private CancellationTokenSource? _idleShutdownCts;

    public bool IsSpeaking => _currentWaveOut?.PlaybackState == PlaybackState.Playing;

    private static readonly string PiperPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Tools\Piper\piper.exe");

    public SpeechSynthesisService(ILogService logger) => _logger = logger;

    // ── Barge-In: немедленная остановка TTS без race condition ───────────────────

    public void Stop()
    {
        _speakCts?.Cancel();
        _kokoroEngine?.StopPlayback();

        var process = _currentProcess;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
        }

        lock (_deviceLock)
        {
            var waveOut = _sharedWaveOut;
            if (waveOut is not null)
            {
                try
                {
                    if (waveOut.PlaybackState != PlaybackState.Stopped)
                        waveOut.Stop();
                }
                catch { }
            }
            // Сбрасываем недоигранный PCM — прерванная фраза не должна возобновиться
            try { _sharedProvider?.ClearBuffer(); } catch { }
        }
    }

    // ── Синтез и воспроизведение ─────────────────────────────────────────────────

    public async Task SpeakAsync(
        string text, string modelPath,
        double speed  = 1.0, double volume = 1.0,
        CancellationToken ct = default)
    {
        Stop();
        await _lock.WaitAsync(ct).ConfigureAwait(false);

        Process? process = null;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _speakCts = linked;

            // Kokoro-82M через KokoroSharp: определяем по расширению .bin (voice name encoding)
            if (modelPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            {
                var voiceName = Path.GetFileNameWithoutExtension(modelPath);
                await SpeakKokoroAsync(text, voiceName, (float)speed, (float)volume, linked.Token)
                    .ConfigureAwait(false);
                return;
            }

            if (!File.Exists(PiperPath))
            {
                await _logger.LogErrorAsync(nameof(SpeechSynthesisService),
                    $"piper.exe не найден: {PiperPath}").ConfigureAwait(false);
                return;
            }

            if (!File.Exists(modelPath))
            {
                await _logger.LogErrorAsync(nameof(SpeechSynthesisService),
                    $"Голосовая модель не найдена: {modelPath}").ConfigureAwait(false);
                return;
            }

            TryGetSampleRateFromConfig(modelPath, out var sampleRate);

            // Первый запуск: прогрев звуковой карты тишиной — устраняет "проглатывание" первого слова
            if (!_warmupDone)
            {
                _warmupDone = true;
                await Task.Run(() => WarmupAudioDevice(sampleRate)).ConfigureAwait(false);
            }

            // length_scale: скорость 1.0 → 1.0; 1.5 → 0.5 (быстрее); 0.5 → 1.5 (медленнее)
            var lengthScale = (2.0 - Math.Clamp(speed, 0.5, 1.5))
                .ToString("F3", CultureInfo.InvariantCulture);

            var psi = new ProcessStartInfo
            {
                FileName               = PiperPath,
                Arguments              = $"--model \"{modelPath}\" --output_raw --length_scale {lengthScale}",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                StandardInputEncoding  = Encoding.UTF8,
            };

            process         = Process.Start(psi)
                ?? throw new InvalidOperationException("Не удалось запустить piper.exe");
            _currentProcess = process;

            var (waveOut, buffered, isNewDevice) = AcquireDevice(sampleRate, volume);
            _currentWaveOut = waveOut;

            var minBytes = (int)(buffered.WaveFormat.AverageBytesPerSecond * 0.35);

            // Логируем только реальную инициализацию устройства — тёплое переиспользование не спамит
            if (isNewDevice)
                await _logger.LogInfoAsync(nameof(SpeechSynthesisService),
                    $"TTS Init → Model: {Path.GetFileName(modelPath)} | " +
                    $"SampleRate: {sampleRate} Hz | PreBuffer: {minBytes} bytes (~350 ms) | " +
                    $"WaveOut: {waveOut.PlaybackState}").ConfigureAwait(false);

            // Текст → stdin; Piper синтезирует PCM и завершается
            await process.StandardInput.WriteAsync(text.AsMemory(), linked.Token)
                .ConfigureAwait(false);
            process.StandardInput.Close();

            await ReadAndPlayPcmAsync(
                process.StandardOutput.BaseStream, buffered, waveOut, minBytes, linked.Token)
                .ConfigureAwait(false);

            try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Сбрасываем недоигранный PCM — устройство остаётся живым, но без хвоста фразы
            lock (_deviceLock)
            {
                try { _sharedProvider?.ClearBuffer(); } catch { }
            }
            await _logger.LogErrorAsync(nameof(SpeechSynthesisService),
                "Ошибка синтеза речи TTS.", ex).ConfigureAwait(false);
        }
        finally
        {
            _currentProcess = null;
            _currentWaveOut = null;
            _speakCts       = null;

            try { process?.Kill(); } catch { }
            process?.Dispose();

            // Демпфирование: WaveOut не закрываем сразу — ждём следующее предложение IdleGraceMs.
            // Заодно дозвучивает хвост из аппаратного буфера, который раньше срезался Dispose().
            ScheduleIdleShutdown();

            _lock.Release();
        }
    }

    // ── Kokoro-82M через KokoroSharp ─────────────────────────────────────────────

    private async Task SpeakKokoroAsync(
        string text, string voiceName, float speed, float volume, CancellationToken ct)
    {
        // Ленивая инициализация KokoroTTS
        if (_kokoroEngine is null)
        {
            if (!File.Exists(KokoroModelPath))
            {
                await _logger.LogErrorAsync(nameof(SpeechSynthesisService),
                    $"[Kokoro] Модель не найдена: {KokoroModelPath}. " +
                    "Скачайте kokoro-v1.0.onnx и положите в Models/TTS/Kokoro/. " +
                    "https://huggingface.co/hexgrad/Kokoro-82M/resolve/main/kokoro-v1.0.onnx")
                    .ConfigureAwait(false);
                return;
            }
            _kokoroEngine = await Task.Run(() => new KokoroTTS(KokoroModelPath), ct)
                .ConfigureAwait(false);
            await _logger.LogInfoAsync(nameof(SpeechSynthesisService),
                $"[Kokoro TTS] Движок инициализирован: {Path.GetFileName(KokoroModelPath)}")
                .ConfigureAwait(false);
        }

        // Получение голоса (NuGet-пакет копирует voices/*.npy в outputDir)
        if (KokoroVoiceManager.Voices.Count == 0)
            await Task.Run(() => KokoroVoiceManager.LoadVoicesFromPath(), ct).ConfigureAwait(false);

        KokoroVoice? voice = null;
        try { voice = KokoroVoiceManager.GetVoice(voiceName); }
        catch { voice = KokoroVoiceManager.Voices.FirstOrDefault(); }

        if (voice is null)
        {
            await _logger.LogErrorAsync(nameof(SpeechSynthesisService),
                $"[Kokoro] Голос '{voiceName}' не найден. " +
                "Убедитесь, что папка 'voices/' скопирована в директорию сборки.").ConfigureAwait(false);
            return;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(SpeechCompletionPacket _)  => tcs.TrySetResult(true);
        void OnCanceled(SpeechCancellationPacket _) => tcs.TrySetResult(false);

        _kokoroEngine.OnSpeechCompleted += OnCompleted;
        _kokoroEngine.OnSpeechCanceled  += OnCanceled;

        var config = new KokoroTTSPipelineConfig { Speed = speed };
        _kokoroEngine.SpeakFast(text, voice, config);

        using var reg = ct.Register(() => { _kokoroEngine.StopPlayback(); tcs.TrySetCanceled(); });

        try { await tcs.Task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            _kokoroEngine.OnSpeechCompleted -= OnCompleted;
            _kokoroEngine.OnSpeechCanceled  -= OnCanceled;
        }
    }

    // ── Тёплое аудиоустройство (переиспользование между предложениями) ───────────

    private (WaveOutEvent WaveOut, BufferedWaveProvider Provider, bool IsNew) AcquireDevice(
        int sampleRate, double volume)
    {
        lock (_deviceLock)
        {
            // Отменяем отложенное закрытие — продолжаем на тёплом устройстве
            _idleShutdownCts?.Cancel();
            _idleShutdownCts = null;

            var vol = (float)Math.Clamp(volume, 0.0, 1.0);

            if (_sharedWaveOut is not null && _sharedSampleRate == sampleRate)
            {
                _sharedWaveOut.Volume = vol;
                return (_sharedWaveOut, _sharedProvider!, false);
            }

            // Смена sample rate (другая голосовая модель) или первый запуск — пересоздаём
            DisposeSharedDeviceLocked();

            var format   = new WaveFormat(sampleRate, 16, 1);
            var provider = new BufferedWaveProvider(format)
            {
                BufferDuration          = TimeSpan.FromSeconds(120),
                DiscardOnBufferOverflow = true,
            };
            var waveOut = new WaveOutEvent { Volume = vol };
            waveOut.Init(provider);

            _sharedWaveOut    = waveOut;
            _sharedProvider   = provider;
            _sharedSampleRate = sampleRate;
            return (waveOut, provider, true);
        }
    }

    private void ScheduleIdleShutdown()
    {
        CancellationToken token;
        lock (_deviceLock)
        {
            if (_sharedWaveOut is null) return;
            _idleShutdownCts?.Cancel();
            _idleShutdownCts = new CancellationTokenSource();
            token = _idleShutdownCts.Token;
        }
        _ = IdleShutdownAsync(token);
    }

    private async Task IdleShutdownAsync(CancellationToken token)
    {
        try { await Task.Delay(IdleGraceMs, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        lock (_deviceLock)
        {
            // Пока ждали — пришло новое предложение (AcquireDevice отменил токен)
            if (token.IsCancellationRequested) return;
            DisposeSharedDeviceLocked();
            _idleShutdownCts = null;
        }
    }

    // Вызывается строго под _deviceLock
    private void DisposeSharedDeviceLocked()
    {
        if (_sharedWaveOut is null) return;
        try { _sharedWaveOut.Stop(); } catch { }
        _sharedWaveOut.Dispose();
        _sharedWaveOut    = null;
        _sharedProvider   = null;
        _sharedSampleRate = 0;
    }

    // ── PCM-стриминг из stdout Piper (ArrayPool, zero-alloc) ─────────────────────

    private static async Task ReadAndPlayPcmAsync(
        Stream               stdout,
        BufferedWaveProvider buffered,
        WaveOutEvent         waveOut,
        int                  minBytes,
        CancellationToken    ct)
    {
        var readBuf = ArrayPool<byte>.Shared.Rent(4096);
        var started = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stdout.ReadAsync(readBuf.AsMemory(0, 4096), ct)
                    .ConfigureAwait(false);
                if (n == 0) break;

                buffered.AddSamples(readBuf, 0, n);

                if (!started && buffered.BufferedBytes >= minBytes)
                {
                    waveOut.Play();   // no-op если тёплое устройство уже играет
                    started = true;
                }
            }

            if (!started && buffered.BufferedBytes > 0)
            {
                waveOut.Play();
                started = true;
            }

            // Ждём дренирования буфера
            if (started)
            {
                while (!ct.IsCancellationRequested
                    && waveOut.PlaybackState == PlaybackState.Playing
                    && buffered.BufferedBytes > 0)
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuf);
        }
    }

    // ── Парсинг audio.sample_rate из .onnx.json ──────────────────────────────────

    private bool TryGetSampleRateFromConfig(string modelPath, out int sampleRate)
    {
        sampleRate = 22050;
        var jsonPath = modelPath + ".json";

        if (!File.Exists(jsonPath))
        {
            _ = _logger.LogWarningAsync(nameof(SpeechSynthesisService),
                $"Конфигурация модели не найдена: '{jsonPath}'. Используется fallback: {sampleRate} Hz.");
            return false;
        }

        try
        {
            using var stream = File.OpenRead(jsonPath);
            using var doc    = JsonDocument.Parse(stream);
            sampleRate = doc.RootElement
                .GetProperty("audio")
                .GetProperty("sample_rate")
                .GetInt32();
            return true;
        }
        catch (Exception ex)
        {
            _ = _logger.LogWarningAsync(nameof(SpeechSynthesisService),
                $"Ошибка парсинга '{jsonPath}'. Используется fallback: {sampleRate} Hz. Причина: {ex.Message}");
            return false;
        }
    }

    // ── Прогрев звукового устройства (первый запуск, Thread Pool) ────────────────

    private static void WarmupAudioDevice(int sampleRate)
    {
        try
        {
            var fmt      = new WaveFormat(sampleRate, 16, 1);
            using var wo = new WaveOutEvent();
            var provider = new BufferedWaveProvider(fmt) { DiscardOnBufferOverflow = true };
            var silence  = new byte[(int)(sampleRate * 2 * 0.15)]; // 150 мс тишины
            provider.AddSamples(silence, 0, silence.Length);
            wo.Init(provider);
            wo.Play();
            Thread.Sleep(150);
        }
        catch { }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        Stop();
        lock (_deviceLock)
        {
            _idleShutdownCts?.Cancel();
            _idleShutdownCts = null;
            DisposeSharedDeviceLocked();
        }
        _kokoroEngine?.Dispose();
        _lock.Dispose();
    }
}
