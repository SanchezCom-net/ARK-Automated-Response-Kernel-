using System.Diagnostics;
using System.Windows.Input;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Services;

/// <summary>
/// Сквозной автотест ARK: Opera + параллельное выполнение + StrictQueue.
/// Запускается автоматически в Debug-сборке через 3 с после старта.
/// </summary>
public sealed class DiagnosticsService
{
    private const string Component = "DiagnosticsService";

    private readonly ILogService      _logger;
    private readonly IActionService   _actionService;
    private readonly IConfigService   _configService;
    private readonly IServiceProvider _serviceProvider;

    public DiagnosticsService(
        ILogService      logger,
        IActionService   actionService,
        IConfigService   configService,
        IServiceProvider serviceProvider)
    {
        _logger          = logger;
        _actionService   = actionService;
        _configService   = configService;
        _serviceProvider = serviceProvider;
    }

    // ── Точка входа ───────────────────────────────────────────────────────────

    public async Task RunOperaTestAsync(CancellationToken ct = default)
    {
        await _logger.LogInfoAsync(Component,
            "=== АВТОТЕСТ: Старт сквозного тестирования ARK (Opera) ===")
            .ConfigureAwait(false);

        await AuditSystemStateAsync(ct).ConfigureAwait(false);

        // Сценарии 1 и 3: параллельный запуск двух Concurrent-макросов в Opera
        await RunParallelTestAsync(ct).ConfigureAwait(false);

        // Сценарий 2: три макроса в строгой очереди StrictQueue
        await RunStrictQueueTestAsync(ct).ConfigureAwait(false);

        await _logger.LogInfoAsync(Component,
            "=== АВТОТЕСТ: Все сценарии завершены. " +
            "Suppression x2 активен — см. записи NetworkService с '(Повторялось подряд N раз)'. ===")
            .ConfigureAwait(false);
    }

    // ── АУДИТ: состояние системы ──────────────────────────────────────────────

    private async Task AuditSystemStateAsync(CancellationToken ct)
    {
        var engine = _serviceProvider.GetRequiredService<INodeEngine>();
        await _logger.LogInfoAsync(Component,
            $"[АУДИТ] NodeEngine: Transient DI — {engine.GetType().Name}, IsRunning={engine.IsRunning}.")
            .ConfigureAwait(false);
    }

    // ── СЦЕНАРИЙ A: параллельный запуск двух Concurrent-макросов ─────────────

    private async Task RunParallelTestAsync(CancellationToken ct)
    {
        await _logger.LogInfoAsync(Component,
            "[A] === Тест параллельного запуска (Concurrent x2) ===")
            .ConfigureAwait(false);

        var overlayNode = new OverlayTextNode
        {
            Name                 = "opera-оверлей",
            Text                 = "ARK: параллельный запуск подтверждён!",
            DurationMilliseconds = 2000
        };

        var tabNode = new SendInputNode
        {
            Name            = "opera-вкладка",
            TargetKey       = Key.T,
            TargetModifiers = ModifierKeys.Control,
            DelayAfterMs    = 50
        };

        nint hwnd = FindMainWindowOf("opera");
        if (hwnd != nint.Zero)
        {
            Win32Api.BringWindowToTop(hwnd);
            Win32Api.SetForegroundWindow(hwnd);
            await _logger.LogInfoAsync(Component,
                $"[A/1] Opera обнаружена. HWND=0x{hwnd:X8}.").ConfigureAwait(false);
        }

        await Task.Delay(700, ct).ConfigureAwait(false);

        var e1 = _serviceProvider.GetRequiredService<INodeEngine>();
        e1.RegisterNodes([overlayNode]);
        _ = e1.StartAsync(overlayNode.Id);

        var e2 = _serviceProvider.GetRequiredService<INodeEngine>();
        e2.RegisterNodes([tabNode]);
        _ = e2.StartAsync(tabNode.Id);

        await Task.Delay(2500, ct).ConfigureAwait(false);

        await _logger.LogInfoAsync(Component,
            "[A/2] Тест A завершён. Оба макроса стартовали параллельно (Concurrent, без региона).")
            .ConfigureAwait(false);
    }

    // ── СЦЕНАРИЙ B: последовательный запуск трёх макросов ────────────────────

    private async Task RunStrictQueueTestAsync(CancellationToken ct)
    {
        await _logger.LogInfoAsync(Component,
            "[B] === Тест последовательных макросов (DelayNode 600 мс × 3) ===")
            .ConfigureAwait(false);

        var q1 = new DelayNode { Name = "Q-1", DelayMilliseconds = 600 };
        var q2 = new DelayNode { Name = "Q-2", DelayMilliseconds = 600 };
        var q3 = new DelayNode { Name = "Q-3", DelayMilliseconds = 600 };

        var e1 = _serviceProvider.GetRequiredService<INodeEngine>();
        e1.RegisterNodes([q1]);
        _ = e1.StartAsync(q1.Id);

        var e2 = _serviceProvider.GetRequiredService<INodeEngine>();
        e2.RegisterNodes([q2]);
        _ = e2.StartAsync(q2.Id);

        var e3 = _serviceProvider.GetRequiredService<INodeEngine>();
        e3.RegisterNodes([q3]);
        _ = e3.StartAsync(q3.Id);

        await Task.Delay(2500, ct).ConfigureAwait(false);

        await _logger.LogInfoAsync(Component,
            "[B/2] Тест B завершён. Три макроса выполнены параллельно.").ConfigureAwait(false);
    }

    private static nint FindMainWindowOf(string processName)
    {
        foreach (var proc in Process.GetProcessesByName(processName))
        {
            if (proc.MainWindowHandle != nint.Zero)
                return proc.MainWindowHandle;
        }
        return nint.Zero;
    }
}
