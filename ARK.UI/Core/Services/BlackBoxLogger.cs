using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

public sealed class BlackBoxLogger : IBlackBoxLogger, IAsyncDisposable
{
    private sealed record BlackBoxEntry(Guid NodeId, DateTime Timestamp, string Message, string? Exception);

    private readonly Channel<BlackBoxEntry> _channel =
        Channel.CreateUnbounded<BlackBoxEntry>(
            new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    private readonly string    _logDirectory;
    private readonly Task      _consumerTask;
    private readonly CancellationTokenSource _cts = new();

    public BlackBoxLogger(ILogService logService)
    {
        _logDirectory = logService.LogDirectory;
        _consumerTask = Task.Run(() => ConsumeAsync(_cts.Token));
    }

    // ── Публичный API ─────────────────────────────────────────────────────────

    public void Log(Guid nodeId, string message, Exception? ex = null)
    {
        // TryWrite никогда не блокирует (Unbounded channel)
        _channel.Writer.TryWrite(new BlackBoxEntry(
            nodeId,
            DateTime.UtcNow,
            message,
            ex?.ToString()));
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        _channel.Writer.TryComplete();
        await _consumerTask.WaitAsync(ct).ConfigureAwait(false);
    }

    // ── Фоновый потребитель ───────────────────────────────────────────────────

    private async Task ConsumeAsync(CancellationToken ct)
    {
        const int batchSize = 32;
        var batch = new List<BlackBoxEntry>(batchSize);

        await foreach (var entry in _channel.Reader.ReadAllAsync(ct))
        {
            batch.Add(entry);

            // Дренируем всё что успело накопиться
            while (batch.Count < batchSize && _channel.Reader.TryRead(out var next))
                batch.Add(next);

            await FlushBatchAsync(batch).ConfigureAwait(false);
            batch.Clear();
        }

        // Дочитываем остаток после TryComplete
        while (_channel.Reader.TryRead(out var remaining))
            batch.Add(remaining);

        if (batch.Count > 0)
            await FlushBatchAsync(batch).ConfigureAwait(false);
    }

    private async Task FlushBatchAsync(List<BlackBoxEntry> batch)
    {
        try
        {
            string path = Path.Combine(
                _logDirectory,
                $"blackbox_{DateTime.UtcNow:yyyy-MM-dd}.ndjson");

            var lines = batch.Select(e => JsonSerializer.Serialize(new
            {
                ts        = e.Timestamp.ToString("O"),
                node_id   = e.NodeId,
                message   = e.Message,
                exception = e.Exception
            }));

            await File.AppendAllLinesAsync(path, lines).ConfigureAwait(false);
        }
        catch
        {
            // Диск недоступен — молча пропускаем батч (не роняем приложение)
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _channel.Writer.TryComplete();
        try { await _consumerTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
