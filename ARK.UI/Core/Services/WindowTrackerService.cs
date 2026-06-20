using System.Diagnostics;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

public sealed class WindowTrackerService : IWindowTrackerService, IDisposable
{
    private const string Component = "WindowTrackerService";

    private readonly ILogService _logger;
    private CancellationTokenSource? _cts;

    public event EventHandler<ActiveWindowInfo>? ActiveWindowChanged;

    public WindowTrackerService(ILogService logger) => _logger = logger;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public void Stop() => _cts?.Cancel();

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await _logger.LogInfoAsync(Component, "Фоновой опрос активного окна запущен (интервал 500 мс).")
            .ConfigureAwait(false);

        using var timer     = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        string?   lastProc  = null;
        string?   lastTitle = null;

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var hwnd = Win32Api.GetForegroundWindow();
                    if (hwnd == IntPtr.Zero) continue;

                    Win32Api.GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == 0) continue;

                    string processName;
                    try
                    {
                        processName = Process.GetProcessById((int)pid).ProcessName + ".exe";
                    }
                    catch
                    {
                        continue;
                    }

                    var title = GetWindowTitle(hwnd);

                    if (!string.Equals(processName, lastProc,  StringComparison.OrdinalIgnoreCase)
                     || !string.Equals(title,       lastTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        lastProc  = processName;
                        lastTitle = title;

                        var info = new ActiveWindowInfo(processName, title);
                        ActiveWindowChanged?.Invoke(this, info);
                        _logger.ResetLogSuppression("TRACKER_ERROR");

                        await _logger.LogInfoAsync(Component,
                            $"Активное окно: [{processName}] \"{title}\"").ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    await _logger.LogErrorSuppressedAsync("TRACKER_ERROR", Component,
                        "Ошибка при опросе активного окна.", ex).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }

        await _logger.LogInfoAsync(Component, "Фоновой опрос активного окна остановлен.")
            .ConfigureAwait(false);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var buf = new char[256];
        int len = Win32Api.GetWindowText(hwnd, buf, 256);
        return len > 0 ? new string(buf, 0, len) : string.Empty;
    }

    public void Dispose()
    {
        if (_cts is not null)
        {
            try
            {
                if (!_cts.IsCancellationRequested) _cts.Cancel();
            }
            catch (ObjectDisposedException) { }
            finally
            {
                _cts.Dispose();
                _cts = null;
            }
        }
        GC.SuppressFinalize(this);
    }
}
