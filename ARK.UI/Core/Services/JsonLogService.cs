using System.Collections.Concurrent;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

public sealed class JsonLogService : ILogService, IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly string _logsDirectory;
    private bool _disposed;

    // ── Состояние подавления повторяющихся логов ──────────────────────────────
    // Один объект на уникальный categoryKey; кэшируется после первого создания.
    private readonly ConcurrentDictionary<string, LogSuppressionState> _suppressedLogs = new();

    private sealed class LogSuppressionState
    {
        // Пишется только под lock(this); читается вне lock (int-reads атомарны на x64).
        public int  IntervalSeconds = 1;
        // Инкрементируется через Interlocked (горячий путь, без lock).
        public int  SuppressedCount;
        // Читается/пишется через Volatile (гарантия видимости без lock на горячем пути).
        public long LastLoggedTicks;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() }
    };

    public string LogDirectory => _logsDirectory;

    public JsonLogService()
    {
        _logsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(_logsDirectory);
    }

    public async Task LogAsync(LogLevel level, string component, string message,
        Exception? exception = null, CancellationToken cancellationToken = default)
    {
        var entry = new LogEntry(
            Timestamp: DateTime.Now,
            Level: level,
            Component: component,
            Message: message,
            Exception: exception?.ToString()
        );

        var json = JsonSerializer.Serialize(entry, JsonOptions);
        var filePath = Path.Combine(_logsDirectory, $"log_{DateTime.Now:yyyy-MM-dd}.json");

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(filePath, json + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ── Экспоненциальное подавление (ILogService) ─────────────────────────────

    public ValueTask LogSuppressedAsync(string categoryKey, LogLevel level, string component,
        string message, Exception? exception = null)
    {
        var state    = _suppressedLogs.GetOrAdd(categoryKey, _ => new LogSuppressionState());
        var nowTicks = DateTime.UtcNow.Ticks;

        // ── Горячий путь: suppress без lock, zero-allocation ──────────────────
        if ((nowTicks - Volatile.Read(ref state.LastLoggedTicks)) / TimeSpan.TicksPerSecond
            < state.IntervalSeconds)
        {
            Interlocked.Increment(ref state.SuppressedCount);
            return ValueTask.CompletedTask;
        }

        // ── Холодный путь: обновляем состояние под lock (вызывается редко) ────
        string finalMessage;
        lock (state)
        {
            // Double-check: другой поток мог уже залогировать и удвоить интервал.
            nowTicks = DateTime.UtcNow.Ticks;
            if ((nowTicks - Volatile.Read(ref state.LastLoggedTicks)) / TimeSpan.TicksPerSecond
                < state.IntervalSeconds)
            {
                Interlocked.Increment(ref state.SuppressedCount);
                return ValueTask.CompletedTask;
            }

            var suppressed = Interlocked.Exchange(ref state.SuppressedCount, 0);
            finalMessage = suppressed > 0
                ? $"{message} (Повторялось подряд {suppressed} раз за последние {state.IntervalSeconds} сек)"
                : message;

            Volatile.Write(ref state.LastLoggedTicks, nowTicks);
            state.IntervalSeconds = Math.Min(state.IntervalSeconds * 2, 300);
        }

        return new ValueTask(LogAsync(level, component, finalMessage, exception));
    }

    public void ResetLogSuppression(string categoryKey)
        => _suppressedLogs.TryRemove(categoryKey, out _);

    // ── IDisposable / IAsyncDisposable ────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
