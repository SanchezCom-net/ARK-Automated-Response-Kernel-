using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ARK.Voice.Worker;

/// <summary>
/// Двусторонний Named Pipe транспорт для WhisperHost.
///   audio-pipe — IN  (ARK пишет PCM16, WhisperHost читает)
///   ctrl-pipe  — OUT (WhisperHost пишет NDJSON-результаты, ARK читает)
///
/// Имена каналов должны точно совпадать с константами в ExternalWhisperService.cs:
///   "ark-whisper-audio-{pipeId}" / "ark-whisper-ctrl-{pipeId}"
/// </summary>
internal sealed class PipeTransport : IDisposable
{
    internal const string AudioPipePrefix = "ark-whisper-audio-";
    internal const string CtrlPipePrefix  = "ark-whisper-ctrl-";

    /// <summary>Магическое значение = команда SHUTDOWN от ARK (BitConverter.GetBytes(-1)).</summary>
    internal const int ShutdownMagic = -1;

    private static readonly Encoding Utf8NoBom =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly NamedPipeClientStream _audioPipe;
    private readonly NamedPipeClientStream _ctrlPipe;
    private StreamWriter? _ctrlWriter;

    internal PipeTransport(string pipeId)
    {
        _audioPipe = new NamedPipeClientStream(".", $"{AudioPipePrefix}{pipeId}",
            PipeDirection.In,  PipeOptions.Asynchronous);
        _ctrlPipe  = new NamedPipeClientStream(".", $"{CtrlPipePrefix}{pipeId}",
            PipeDirection.Out, PipeOptions.Asynchronous);
    }

    internal async Task ConnectAsync(CancellationToken ct)
    {
        await _audioPipe.ConnectAsync(ct).ConfigureAwait(false);
        await _ctrlPipe .ConnectAsync(ct).ConfigureAwait(false);

        // StreamWriter создаётся строго после ConnectAsync — иначе Flush() бросает InvalidOperationException.
        _ctrlWriter = new StreamWriter(_ctrlPipe, Utf8NoBom, leaveOpen: true) { AutoFlush = true };
    }

    // ── Чтение PCM ────────────────────────────────────────────────────────────

    /// <summary>
    /// Читает один PCM16-чанк. Протокол: [4-byte Int32 len][PCM bytes].
    /// Возвращает empty[] при SHUTDOWN, null при ошибке/закрытии канала.
    /// </summary>
    internal async Task<short[]?> ReadPcmChunkAsync(CancellationToken ct)
    {
        try
        {
            var lenBuf = new byte[4];
            await _audioPipe.ReadExactlyAsync(lenBuf, ct).ConfigureAwait(false);
            int byteCount = BitConverter.ToInt32(lenBuf, 0);

            if (byteCount == ShutdownMagic) return Array.Empty<short>();
            if (byteCount <= 0) return null;

            var pcmBytes = new byte[byteCount];
            await _audioPipe.ReadExactlyAsync(pcmBytes, ct).ConfigureAwait(false);

            var shorts = new short[byteCount / 2];
            Buffer.BlockCopy(pcmBytes, 0, shorts, 0, byteCount);
            return shorts;
        }
        catch (EndOfStreamException)       { return null; }
        catch (OperationCanceledException) { return null; }
        catch (IOException)                { return null; }
    }

    // ── Запись результатов ────────────────────────────────────────────────────

    internal void WriteResult(string text)
    {
        if (_ctrlWriter is not { } w) return;
        try { w.WriteLine($"{{\"type\":\"result\",\"text\":{JsonSerializer.Serialize(text)}}}"); }
        catch (IOException) { }
    }

    internal void WriteStatus(string value)
    {
        if (_ctrlWriter is not { } w) return;
        try { w.WriteLine($"{{\"type\":\"status\",\"value\":\"{value}\"}}"); }
        catch (IOException) { }
    }

    internal void WriteLog(string level, string message)
    {
        if (_ctrlWriter is not { } w) return;
        try
        {
            w.WriteLine(
                $"{{\"type\":\"log\",\"level\":\"{level}\"," +
                $"\"message\":{JsonSerializer.Serialize(message)}}}");
        }
        catch (IOException) { }
    }

    /// <summary>
    /// Отправляет команду HALT в ARK.UI: неустранимая ошибка, перезапускать не нужно.
    /// ARK.UI немедленно устанавливает Faulted и переключается на резервный движок.
    /// </summary>
    internal void WriteHalt(string message)
    {
        if (_ctrlWriter is not { } w) return;
        try
        {
            w.WriteLine($"{{\"type\":\"halt\",\"message\":{JsonSerializer.Serialize(message)}}}");
            w.Flush();
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
