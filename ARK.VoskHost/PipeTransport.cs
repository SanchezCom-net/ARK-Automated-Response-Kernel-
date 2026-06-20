using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ARK.VoskHost;

/// <summary>
/// Управляет подключением к двум Named Pipe каналам ARK.UI:
///   audio-pipe  — IN  (ARK пишет PCM16, VoskHost читает)
///   ctrl-pipe   — OUT (VoskHost пишет NDJSON-результаты, ARK читает)
/// ARK.UI является сервером (NamedPipeServerStream), VoskHost — клиентом.
///
/// ВАЖНО: StreamWriter (_ctrlWriter) создаётся только ПОСЛЕ ConnectAsync —
/// сеттер AutoFlush=true вызывает Flush() внутри StreamWriter, что вызывало
/// InvalidOperationException("Pipe hasn't been connected yet") при создании
/// в конструкторе до вызова NamedPipeClientStream.ConnectAsync().
/// </summary>
internal sealed class PipeTransport : IDisposable
{
    private const string AudioPipePrefix = "ark-vosk-audio-";
    private const string CtrlPipePrefix  = "ark-vosk-ctrl-";

    /// <summary>
    /// Магическое значение заголовка длины, означающее команду SHUTDOWN от ARK.
    /// ARK пишет BitConverter.GetBytes(-1) в audio-pipe вместо длины PCM-чанка.
    /// VoskHost: получить → FinalResult() → WriteResult() → завершить работу.
    /// </summary>
    internal const int ShutdownMagic = -1;

    // UTF-8 без BOM: NDJSON-поток не требует BOM; наличие BOM ломает JsonDocument.Parse
    // на первой строке в ARK.UI ReadCtrlPipeLoopAsync.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly NamedPipeClientStream _audioPipe;
    private readonly NamedPipeClientStream _ctrlPipe;

    // Инициализируется только после ConnectAsync — не в конструкторе.
    private StreamWriter? _ctrlWriter;

    internal PipeTransport(string pipeId)
    {
        _audioPipe = new NamedPipeClientStream(".", $"{AudioPipePrefix}{pipeId}",
            PipeDirection.In,  PipeOptions.Asynchronous);
        _ctrlPipe  = new NamedPipeClientStream(".", $"{CtrlPipePrefix}{pipeId}",
            PipeDirection.Out, PipeOptions.Asynchronous);
    }

    /// <summary>
    /// Подключается к обоим каналам, затем создаёт StreamWriter.
    /// Таймаут задаётся через CancellationToken.
    /// </summary>
    internal async Task ConnectAsync(CancellationToken ct)
    {
        await _audioPipe.ConnectAsync(ct).ConfigureAwait(false);
        await _ctrlPipe .ConnectAsync(ct).ConfigureAwait(false);

        // StreamWriter создаётся строго здесь — оба pipe уже подключены.
        // AutoFlush=true безопасен: Flush() вызывается на уже открытом соединении.
        _ctrlWriter = new StreamWriter(_ctrlPipe, Utf8NoBom, leaveOpen: true)
            { AutoFlush = true };
    }

    // ── Чтение аудио ──────────────────────────────────────────────────────────

    /// <summary>
    /// Читает один PCM16-чанк из audio-pipe.
    /// Протокол: [4 байта little-endian Int32 = количество байт] [PCM байты]
    /// Возвращает null при закрытии канала.
    /// </summary>
    internal async Task<short[]?> ReadPcmChunkAsync(CancellationToken ct)
    {
        try
        {
            var lenBuf = new byte[4];
            await _audioPipe.ReadExactlyAsync(lenBuf, ct).ConfigureAwait(false);
            int byteCount = BitConverter.ToInt32(lenBuf, 0);
            // ShutdownMagic (-1): ARK инициирует Graceful Shutdown → вернуть пустой массив как сигнал.
            if (byteCount == ShutdownMagic) return Array.Empty<short>();
            if (byteCount <= 0) return null;

            var pcmBytes = new byte[byteCount];
            await _audioPipe.ReadExactlyAsync(pcmBytes, ct).ConfigureAwait(false);

            var shorts = new short[byteCount / 2];
            Buffer.BlockCopy(pcmBytes, 0, shorts, 0, byteCount);
            return shorts;
        }
        catch (EndOfStreamException)        { return null; }
        catch (OperationCanceledException)  { return null; }
        catch (IOException)                 { return null; }
    }

    // ── Запись результатов ────────────────────────────────────────────────────

    /// <summary>Отправляет результат распознавания (NDJSON-строка).</summary>
    internal void WriteResult(string text)
    {
        if (_ctrlWriter is not { } w) return;
        try
        {
            w.WriteLine($"{{\"type\":\"result\",\"text\":{JsonSerializer.Serialize(text)}}}");
        }
        catch (IOException) { }
    }

    /// <summary>Отправляет статусное сообщение (Pause / Resume).</summary>
    internal void WriteStatus(string value)
    {
        if (_ctrlWriter is not { } w) return;
        try
        {
            w.WriteLine($"{{\"type\":\"status\",\"value\":\"{value}\"}}");
        }
        catch (IOException) { }
    }

    /// <summary>Отправляет лог-сообщение в ARK.UI для записи через ILogService.</summary>
    internal void WriteLog(string level, string message)
    {
        if (_ctrlWriter is not { } w) return;
        try
        {
            w.WriteLine(
                $"{{\"type\":\"log\",\"level\":\"{level}\",\"message\":{JsonSerializer.Serialize(message)}}}");
        }
        catch (IOException) { }
    }

    public void Dispose()
    {
        _ctrlWriter?.Dispose();
        _audioPipe .Dispose();
        _ctrlPipe  .Dispose();
    }
}
