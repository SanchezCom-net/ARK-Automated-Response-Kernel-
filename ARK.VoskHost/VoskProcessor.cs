using System.Text.Json;
using Vosk;

namespace ARK.VoskHost;

/// <summary>
/// Основной цикл VoskHost: читает PCM-чанки из PipeTransport,
/// выполняет VAD-мониторинг и распознавание через Vosk,
/// отправляет результаты и статусы обратно в ARK.UI.
///
/// Протокол (один чанк = один результат):
///   ARK → VoskHost: [4-byte Int32 pcmByteCount][pcm bytes]  — нормальный аудио-чанк
///                   [4-byte Int32 = -1]                      — команда SHUTDOWN (Graceful Shutdown)
///   VoskHost → ARK: {"type":"result","text":"..."}
///                   {"type":"status","value":"Pause/Resume"}  (внеполосное)
/// </summary>
internal sealed class VoskProcessor : IDisposable
{
    private const int    SampleRate = 16_000;
    private const string Component  = "VoskProcessor";

    private readonly PipeTransport _pipes;
    private readonly VadMonitor    _vad   = new();
    private readonly string        _modelPath;
    private readonly string        _language;

    private Model?          _model;
    private VoskRecognizer? _recognizer;
    private bool            _disposed;

    internal VoskProcessor(PipeTransport pipes, string modelPath, string language)
    {
        _pipes     = pipes;
        _modelPath = modelPath;
        _language  = language;
    }

    // ── Инициализация (блокирующая, вызывается до RunLoopAsync) ──────────────

    internal void Initialize()
    {
        Console.Error.WriteLine($"[{Component}] Загрузка модели: {_modelPath}. Язык: {_language}.");
        Vosk.Vosk.SetLogLevel(-1);
        _model      = new Model(_modelPath);
        _recognizer = new VoskRecognizer(_model, SampleRate);
        Console.Error.WriteLine($"[{Component}] Модель загружена. Ожидаю аудио из pipe.");
    }

    // ── Главный цикл обработки ────────────────────────────────────────────────

    internal async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 1. Читаем следующий PCM-чанк или команду управления
            var pcm = await _pipes.ReadPcmChunkAsync(ct).ConfigureAwait(false);

            if (pcm is null)
            {
                // null = ошибка pipe или закрытие канала
                Console.Error.WriteLine($"[{Component}] Audio pipe закрыт. Завершение.");
                break;
            }

            if (pcm.Length == 0)
            {
                // Пустой массив = команда SHUTDOWN от ARK (PipeTransport.ShutdownMagic = -1)
                Console.Error.WriteLine($"[{Component}] SHUTDOWN получен. Сбрасываю буфер и завершаю работу.");
                _pipes.WriteLog("info", "[VoskHost] SHUTDOWN: сбрасываю накопленный буфер.");
                // FinalResult() возвращает всё, что накоплено в recognizer с последнего Reset()
                if (_recognizer is not null)
                {
                    var finalJson = _recognizer.FinalResult();
                    _pipes.WriteResult(ExtractText(finalJson));
                }
                break;
            }

            // 2. Диагностика: подтверждаем получение аудио-данных
            _pipes.WriteLog("info", "[VoskHost] Аудио поток получен");

            // 3. VAD-мониторинг шума
            double durationSec = (double)pcm.Length / SampleRate;
            var (changed, newState) = _vad.Process(pcm, durationSec);
            if (changed)
            {
                Console.Error.WriteLine($"[{Component}] VAD статус: {newState} " +
                    $"(длительность чанка: {durationSec:F2} с).");
                _pipes.WriteStatus(newState);
            }

            // 4. В режиме Pause — сбрасываем кэш и отправляем пустой результат
            if (_vad.IsPaused)
            {
                _recognizer?.Reset();
                _pipes.WriteResult(string.Empty);
                continue;
            }

            // 5. Распознавание
            var text = RecognizeChunk(pcm);
            if (!string.IsNullOrWhiteSpace(text))
                _pipes.WriteLog("info", $"[VoskHost] Распознано: \"{text}\"");
            _pipes.WriteResult(text);
        }
    }

    // ── Инференс ──────────────────────────────────────────────────────────────

    private string RecognizeChunk(short[] pcm)
    {
        if (_recognizer is null || _disposed) return string.Empty;
        try
        {
            _recognizer.AcceptWaveform(pcm, pcm.Length);
            var json = _recognizer.FinalResult();
            _recognizer.Reset();
            return ExtractText(json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{Component}] Ошибка распознавания: {ex.Message}");
            _recognizer?.Reset();
            return string.Empty;
        }
    }

    private static string ExtractText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("text", out var t)
                ? t.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch { return string.Empty; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recognizer?.Dispose();
        _model?.Dispose();
        _recognizer = null;
        _model      = null;
    }
}
