using System.Text;
using Whisper.net;

namespace ARK.Voice.Worker;

/// <summary>
/// Основной цикл WhisperHost: читает PCM-чанки из PipeTransport, запускает инференс
/// через Whisper.net и отправляет результаты обратно в ARK.UI.
///
/// Поведение при --use-gpu без GPU:
///   WriteHalt → {"type":"halt","message":"[CRITICAL] Whisper: GPU hardware not found..."}
///   ARK.UI переводит IModelManager в Faulted и переключается на Vosk без перезапуска воркера.
/// </summary>
internal sealed class WhisperPipeProcessor : IAsyncDisposable
{
    private const int    SampleRate = 16_000;
    private const string Component  = "WhisperHost";

    private readonly PipeTransport?    _pipes;
    private readonly WhisperWorkerConfig _config;

    private WhisperFactory?   _factory;
    private WhisperProcessor? _processor;
    private bool              _disposed;

    internal WhisperPipeProcessor(PipeTransport? pipes, WhisperWorkerConfig config)
    {
        _pipes  = pipes;
        _config = config;
    }

    // ── Инициализация ─────────────────────────────────────────────────────────

    internal async Task InitializeAsync(CancellationToken ct)
    {
        Console.Error.WriteLine(
            $"[{Component}] Конфиг: model_type={_config.ModelType}, " +
            $"precision={_config.Precision}, use_gpu={_config.UseGpu}, " +
            $"gpu_device={_config.GpuDevice}, language={_config.Language}.");
        Console.Error.WriteLine(
            $"[{Component}] Загрузка: {_config.ModelPath}.");

        await Task.Run(() =>
        {
            if (_config.UseGpu)
            {
                try
                {
                    _factory = WhisperFactory.FromPath(_config.ModelPath,
                        new WhisperFactoryOptions { UseGpu = true, GpuDevice = _config.GpuDevice });
                    Console.Error.WriteLine($"[{Component}] GPU (CUDA, device={_config.GpuDevice}) активирован.");
                }
                catch (Exception gpuEx)
                {
                    // Принудительный GPU-старт: GPU не найден → HALT (не фолбэчим на CPU)
                    var critMsg =
                        $"[CRITICAL] Whisper: GPU hardware not found. " +
                        $"({gpuEx.GetType().Name}: {gpuEx.Message}). " +
                        $"Transitioning to fallback or halting.";

                    // Полный стектрейс — в stderr (виден в ARK.UI через [stderr] строки в логе)
                    Console.Error.WriteLine($"[{Component}] {critMsg}");
                    Console.Error.WriteLine($"[{Component}] GPU init exception (full):\n{gpuEx}");

                    // Краткое + детальное исключение — через ctrl-pipe в основной logs/log_*.json
                    _pipes?.WriteLog("critical", critMsg);
                    _pipes?.WriteLog("critical",
                        $"[GPU init stacktrace] {gpuEx.GetType().FullName}: {gpuEx.Message}" +
                        $"{(gpuEx.InnerException is { } ie ? $" → InnerException: {ie.GetType().Name}: {ie.Message}" : string.Empty)}" +
                        $"\nStackTrace: {gpuEx.StackTrace}");
                    _pipes?.WriteHalt(critMsg);

                    _factory?.Dispose();
                    _factory = null;

                    throw new InvalidOperationException(critMsg, gpuEx);
                }
            }
            else
            {
                try
                {
                    _factory = WhisperFactory.FromPath(_config.ModelPath,
                        new WhisperFactoryOptions { UseGpu = false });
                    Console.Error.WriteLine($"[{Component}] CPU backend активирован.");
                }
                catch (Exception cpuEx)
                {
                    var errMsg =
                        $"[CRITICAL] Whisper: CPU model load failed. " +
                        $"({cpuEx.GetType().Name}: {cpuEx.Message}).";
                    Console.Error.WriteLine($"[{Component}] {errMsg}");
                    Console.Error.WriteLine($"[{Component}] CPU init exception (full):\n{cpuEx}");
                    _pipes?.WriteLog("critical", errMsg);
                    _pipes?.WriteLog("critical",
                        $"[CPU init stacktrace] {cpuEx.GetType().FullName}: {cpuEx.Message}" +
                        $"{(cpuEx.InnerException is { } ie ? $" → InnerException: {ie.GetType().Name}: {ie.Message}" : string.Empty)}" +
                        $"\nStackTrace: {cpuEx.StackTrace}");
                    _pipes?.WriteHalt(errMsg);
                    _factory?.Dispose();
                    _factory = null;
                    throw new InvalidOperationException(errMsg, cpuEx);
                }
            }

            _processor = _factory!.CreateBuilder().WithLanguage(_config.Language).Build();
            Console.Error.WriteLine($"[{Component}] Модель загружена. Ожидаю аудио из pipe.");
        }, ct);
    }

    // ── Главный цикл ──────────────────────────────────────────────────────────

    internal async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pcm = await _pipes!.ReadPcmChunkAsync(ct).ConfigureAwait(false);

            if (pcm is null)
            {
                Console.Error.WriteLine($"[{Component}] Audio pipe закрыт. Завершение.");
                break;
            }

            if (pcm.Length == 0)
            {
                Console.Error.WriteLine($"[{Component}] SHUTDOWN получен. Завершаю работу.");
                _pipes.WriteLog("info", $"[{Component}] SHUTDOWN принят.");
                _pipes.WriteResult(string.Empty);
                break;
            }

            _pipes.WriteLog("info", $"[{Component}] Аудио поток получен");

            var text = await RecognizeAsync(pcm, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
                _pipes.WriteLog("info", $"[{Component}] Распознано: \"{text}\"");
            _pipes.WriteResult(text);
        }
    }

    // ── Инференс ──────────────────────────────────────────────────────────────

    private async Task<string> RecognizeAsync(short[] pcm, CancellationToken ct)
    {
        if (_processor is null || _disposed) return string.Empty;
        try
        {
            using var wav = BuildWavStream(pcm);
            var sb = new StringBuilder();
            await foreach (var seg in _processor.ProcessAsync(wav, ct).ConfigureAwait(false))
                sb.Append(seg.Text);
            return sb.ToString().Trim();
        }
        catch (OperationCanceledException) { return string.Empty; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{Component}] Ошибка инференса: {ex.Message}");
            return string.Empty;
        }
    }

    // Строит RIFF/WAV-контейнер из raw PCM16 short[] (16kHz mono).
    // Whisper.net.WhisperProcessor.ProcessAsync ожидает корректный WAV-поток.
    private static MemoryStream BuildWavStream(short[] pcm)
    {
        int byteCount = pcm.Length * 2;
        var ms = new MemoryStream(44 + byteCount);
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        w.Write("RIFF"u8.ToArray());
        w.Write(36 + byteCount);
        w.Write("WAVE"u8.ToArray());

        w.Write("fmt "u8.ToArray());
        w.Write(16);                  // PCM fmt size
        w.Write((short)1);            // PCM
        w.Write((short)1);            // Mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);      // Byte rate
        w.Write((short)2);            // Block align
        w.Write((short)16);           // Bits per sample

        w.Write("data"u8.ToArray());
        w.Write(byteCount);
        foreach (var s in pcm) w.Write(s);

        ms.Position = 0;
        return ms;
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_processor is not null)
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
            _processor = null;
        }

        _factory?.Dispose();
        _factory = null;
    }
}
