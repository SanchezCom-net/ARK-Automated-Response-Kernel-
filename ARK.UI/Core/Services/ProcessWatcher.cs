using System.Diagnostics;
using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Services;

/// <summary>
/// Периодически (каждые 2 сек) сравнивает снимки списка процессов ОС.
/// Даёт MacroScheduler мгновенный доступ к RunningProcessNames без синхронных вызовов
/// Process.GetProcesses() на горячем пути хоткея.
/// </summary>
public sealed class ProcessWatcher : IProcessWatcher, IDisposable
{
    private const int    PollIntervalMs = 2000;
    private const string Component      = nameof(ProcessWatcher);

    private readonly ILogService _logger;
    private readonly object      _snapshotLock = new();

    private Dictionary<int, string> _previous    = new();
    private HashSet<string>         _runningNames = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private bool _started;

    public event EventHandler<ProcessWatcherEventArgs>? ProcessStarted;
    public event EventHandler<ProcessWatcherEventArgs>? ProcessExited;

    public IReadOnlySet<string> RunningProcessNames
    {
        get { lock (_snapshotLock) return _runningNames; }
    }

    public ProcessWatcher(ILogService logger) => _logger = logger;

    public void Start(CancellationToken ct = default)
    {
        if (_started) return;
        _started = true;

        _cts     = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var snap = TakeSnapshot();
        lock (_snapshotLock)
        {
            _previous     = snap;
            _runningNames = new HashSet<string>(snap.Values, StringComparer.OrdinalIgnoreCase);
        }
        _ = PollLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _started = false;
        try   { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
        _cts?.Dispose();
        _cts = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
                await Task.Run(DiffAndFire, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                "Ошибка фонового опроса процессов.", ex).ConfigureAwait(false);
        }
    }

    private void DiffAndFire()
    {
        var current = TakeSnapshot();

        Dictionary<int, string> previous;
        lock (_snapshotLock) { previous = _previous; }

        foreach (var (pid, name) in current)
            if (!previous.ContainsKey(pid))
                ProcessStarted?.Invoke(this, new ProcessWatcherEventArgs(name, pid));

        foreach (var (pid, name) in previous)
            if (!current.ContainsKey(pid))
                ProcessExited?.Invoke(this, new ProcessWatcherEventArgs(name, pid));

        lock (_snapshotLock)
        {
            _previous     = current;
            _runningNames = new HashSet<string>(current.Values, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<int, string> TakeSnapshot()
    {
        var result = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            try   { result[p.Id] = p.ProcessName; }
            catch { /* процесс завершился во время перечисления */ }
            finally { p.Dispose(); }
        }
        return result;
    }

    public void Dispose() => Stop();
}
