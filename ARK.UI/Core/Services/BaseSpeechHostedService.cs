using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

/// <summary>
/// Абстрактная база для IModelWrapper-реализаций поверх внешнего хост-процесса и Named Pipes.
///
/// Управляет жизненным циклом процесса, именованными каналами (audio + ctrl),
/// watchdog-мониторингом (RAM / сессия / простой) и протоколом Graceful Shutdown.
/// Конкретная реализация обязана определить:
///   AudioPipePrefix / CtrlPipePrefix — должны совпадать с константами в хост-процессе
///   ValidateAsync   — проверка специфических требований движка к модели
///   BuildArguments  — аргументы командной строки для хост-процесса
/// </summary>
public abstract partial class BaseSpeechHostedService : IModelWrapper
{
    internal const int ShutdownMagic = -1; // sentinel audio-pipe: команда SHUTDOWN

    private static readonly TimeSpan RecognizeTimeout     = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MemCheckInterval     = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OrphanKillDelay      = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RestartPipeDelay     = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan GracefulShutdownWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ForceKillWait        = TimeSpan.FromSeconds(3);

    protected readonly ILogService         _logger;
    protected readonly IOverlayService     _overlay;
    protected readonly VoskSettingsSection _settings;

    private readonly SemaphoreSlim   _recognizeLock = new(1, 1);
    private readonly Channel<string> _resultChannel =
        Channel.CreateBounded<string>(new BoundedChannelOptions(2)
            { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly object        _stderrLock   = new();
    private readonly StringBuilder _stderrBuffer = new();

    private NamedPipeServerStream?   _audioPipe;
    private NamedPipeServerStream?   _ctrlPipe;
    private CancellationTokenSource? _ctrlReaderCts;
    private Process?                 _process;
    private System.Threading.Timer?  _memoryTimer;
    private System.Threading.Timer?  _sessionTimer;
    private System.Threading.Timer?  _idleTimer;

    private string _modelPath = string.Empty;
    private string _language  = string.Empty;

    private volatile bool _isReady;
    private volatile bool _disposed;
    private volatile bool _restarting;
    private volatile bool _faulted;
    private int           _failureCount;

    private string PipeId        => $"{Environment.ProcessId}";
    private string AudioPipeName => $"{AudioPipePrefix}{PipeId}";
    private string CtrlPipeName  => $"{CtrlPipePrefix}{PipeId}";

    // ── Абстрактный контракт ──────────────────────────────────────────────────

    public abstract ModelType Type { get; }

    /// <summary>Имя компонента для строк лога. По умолчанию = GetType().Name.</summary>
    protected virtual string ComponentName => GetType().Name;

    /// <summary>Префикс audio Named Pipe — должен точно совпадать с константой в хост-процессе.</summary>
    protected abstract string AudioPipePrefix { get; }

    /// <summary>Префикс ctrl Named Pipe — должен точно совпадать с константой в хост-процессе.</summary>
    protected abstract string CtrlPipePrefix { get; }

    /// <summary>Проверяет требования движка к модели. Возвращает строку ошибки или null при успехе.</summary>
    protected abstract Task<string?> ValidateAsync(string modelPath, CancellationToken ct);

    /// <summary>Строит аргументы командной строки для хост-процесса (без флага --dry-run).</summary>
    protected abstract string BuildArguments(string pipeId, string modelPath, string language);

    /// <summary>
    /// Вызывается непосредственно перед Process.Start. Переопределяется для GPU-задержки,
    /// GC-принуждения и специфической подготовки движка.
    /// </summary>
    protected virtual Task OnBeforeStartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Фильтр строк stderr хост-процесса: true = записать в лог, false = игнорировать.
    /// Переопределяется для подавления информационного шума GPU-инициализации.
    /// </summary>
    protected virtual bool ShouldLogStderr(string line) => true;

    // ── Конструктор ───────────────────────────────────────────────────────────

    public bool IsReady => _isReady && !_faulted;

    /// <summary>Срабатывает однократно, когда хост-процесс перешёл в Faulted (лимит перезапусков исчерпан).</summary>
    public event System.Action? Faulted;

    protected BaseSpeechHostedService(
        ILogService logger, IOverlayService overlay, VoskSettingsSection settings)
    {
        _logger   = logger;
        _overlay  = overlay;
        _settings = settings;
    }

    // ── IModelWrapper: Инициализация ──────────────────────────────────────────

    public async Task InitializeAsync(string modelPath, string language, CancellationToken ct = default)
    {
        _modelPath = modelPath;
        _language  = language;

        var exePath = Path.Combine(AppContext.BaseDirectory, _settings.HostProcessName);
        if (!File.Exists(exePath))
        {
            await _logger.LogErrorAsync(ComponentName,
                $"[CRITICAL] {_settings.HostProcessName} не найден: {exePath}.")
                .ConfigureAwait(false);
            return;
        }

        if (await ValidateAsync(modelPath, ct).ConfigureAwait(false) is { } modelError)
        {
            await _logger.LogErrorAsync(ComponentName, $"[CRITICAL] {modelError}").ConfigureAwait(false);
            return;
        }

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await StartHostProcessAsync(ct).ConfigureAwait(false);
                StartMonitors();
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                await _logger.LogErrorAsync(ComponentName,
                    $"[Init] {_settings.HostProcessName} не запустился (попытка {attempt}/2): {ex.Message}", ex)
                    .ConfigureAwait(false);

                if (attempt < 2 && !ct.IsCancellationRequested)
                    await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }

        await _logger.LogErrorAsync(ComponentName,
            $"[Init] Все попытки запуска {_settings.HostProcessName} исчерпаны.").ConfigureAwait(false);
    }

    // ── IModelWrapper: Распознавание ──────────────────────────────────────────

    public async Task<string> RecognizeAsync(Stream audioWav, CancellationToken ct = default)
    {
        if (_faulted || !_isReady || _restarting) return string.Empty;

        await _recognizeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_isReady || _audioPipe is null) return string.Empty;

            audioWav.Position = 0;
            int pcmOffset    = FindPcmDataOffset(audioWav);
            audioWav.Position = pcmOffset;
            int pcmByteCount = (int)(audioWav.Length - pcmOffset);
            var pcmBytes     = new byte[pcmByteCount];
            await audioWav.ReadExactlyAsync(pcmBytes, ct).ConfigureAwait(false);

            await _audioPipe.WriteAsync(BitConverter.GetBytes(pcmByteCount), ct).ConfigureAwait(false);
            await _audioPipe.WriteAsync(pcmBytes, ct).ConfigureAwait(false);
            await _audioPipe.FlushAsync(ct).ConfigureAwait(false);

            ResetIdleTimer();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RecognizeTimeout);
            try
            {
                return await _resultChannel.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                while (_resultChannel.Reader.TryRead(out _)) { }
                return string.Empty;
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            await _logger.LogErrorAsync(ComponentName, "[HostProcess] Ошибка записи в audio-pipe.", ex)
                .ConfigureAwait(false);
            return string.Empty;
        }
        finally
        {
            _recognizeLock.Release();
        }
    }

    // ── Диагностический сухой запуск ─────────────────────────────────────────

    public async Task LaunchDryRunAsync()
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, _settings.HostProcessName);
        if (!File.Exists(exePath))
        {
            await _logger.LogErrorAsync(ComponentName,
                $"[DryRun] {_settings.HostProcessName} не найден: {exePath}").ConfigureAwait(false);
            return;
        }

        await _logger.LogInfoAsync(ComponentName,
            $"[DryRun] {_settings.HostProcessName}. Модель: {_modelPath}, язык: {_language}.")
            .ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName               = exePath,
            Arguments              = $"--dry-run {BuildArguments("0", _modelPath, _language)}",
            WorkingDirectory       = AppContext.BaseDirectory,
            CreateNoWindow         = false,
            UseShellExecute        = false,
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
        };

        try
        {
            var proc    = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start вернул null.");
            var readOut = proc.StandardOutput.ReadToEndAsync();
            var readErr = proc.StandardError .ReadToEndAsync();
            await Task.WhenAll(readOut, readErr).ConfigureAwait(false);
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(readOut.Result))
                await _logger.LogInfoAsync(ComponentName,    $"[DryRun stdout]\n{readOut.Result.Trim()}").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(readErr.Result))
                await _logger.LogWarningAsync(ComponentName, $"[DryRun stderr]\n{readErr.Result.Trim()}").ConfigureAwait(false);

            await _logger.LogInfoAsync(ComponentName,
                $"[DryRun] Exit={proc.ExitCode}. " + (proc.ExitCode == 0 ? "SUCCESS." : "FAILED."))
                .ConfigureAwait(false);
            proc.Dispose();
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(ComponentName, "[DryRun] Ошибка запуска.", ex).ConfigureAwait(false);
        }
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _isReady  = false;

        _memoryTimer ?.Dispose();
        _sessionTimer?.Dispose();
        _idleTimer   ?.Dispose();
        _ctrlReaderCts?.Cancel();

        await KillProcessAsync().ConfigureAwait(false);

        await _recognizeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _audioPipe?.Dispose(); _audioPipe = null;
            _ctrlPipe ?.Dispose(); _ctrlPipe  = null;
        }
        finally { _recognizeLock.Release(); }

        _ctrlReaderCts?.Dispose();
        _recognizeLock.Dispose();
    }
}
