using System.Diagnostics;
using System.Windows.Input;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
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
    private readonly IMacroScheduler  _scheduler;
    private readonly IConfigService   _configService;
    private readonly IServiceProvider _serviceProvider;

    public DiagnosticsService(
        ILogService      logger,
        IActionService   actionService,
        IMacroScheduler  scheduler,
        IConfigService   configService,
        IServiceProvider serviceProvider)
    {
        _logger          = logger;
        _actionService   = actionService;
        _scheduler       = scheduler;
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

        await _logger.LogInfoAsync(Component,
            $"[АУДИТ] MacroScheduler: профилей={_configService.Current.Profiles.Count}, " +
            $"ActiveProfile='{_scheduler.ActiveProfileName ?? "(нет)"}'.")
            .ConfigureAwait(false);
    }

    // ── СЦЕНАРИЙ A: параллельный запуск свободных макросов в Opera ────────────
    // Сценарий 1 (папка, Concurrent) + Сценарий 3 (корень, свободный запуск):
    //   оба Concurrent-региона стартуют одновременно через Task.WhenAll.

    private async Task RunParallelTestAsync(CancellationToken ct)
    {
        await _logger.LogInfoAsync(Component,
            "[A] === Тест параллельного запуска (Concurrent x2, Opera, Ctrl+F12) ===")
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

        var testProfile = new AppProfile
        {
            FriendlyName      = "OPERA_TEST",
            TargetProcessName = "opera.exe",
            IsGlobal          = true,
            FocusRequired     = false
        };
        testProfile.Regions.Add(MakeConcurrentRegion("opera-оверлей", overlayNode));
        testProfile.Regions.Add(MakeConcurrentRegion("opera-вкладка", tabNode));

        _configService.Current.Profiles.Add(testProfile);
        await _logger.LogInfoAsync(Component,
            "[A/1] Профиль 'OPERA_TEST' зарегистрирован (2 Concurrent-региона, IsGlobal, Ctrl+F12).")
            .ConfigureAwait(false);

        nint hwnd = FindMainWindowOf("opera");
        if (hwnd != nint.Zero)
        {
            Win32Api.BringWindowToTop(hwnd);
            Win32Api.SetForegroundWindow(hwnd);
            await _logger.LogInfoAsync(Component,
                $"[A/2] Opera обнаружена, переведена на передний план. HWND=0x{hwnd:X8}.")
                .ConfigureAwait(false);
        }
        else
        {
            await _logger.LogWarningAsync(Component,
                "[A/2] Opera не обнаружена. Тест продолжается на глобальном профиле.")
                .ConfigureAwait(false);
        }

        await Task.Delay(700, ct).ConfigureAwait(false);
        await _logger.LogInfoAsync(Component,
            $"[A/3] ActiveProfile='{_scheduler.ActiveProfileName ?? "(нет)"}'. Эмулируем Ctrl+F12...")
            .ConfigureAwait(false);

        // SendInput → WH_KEYBOARD_LL hook → MacroScheduler → Task.WhenAll (оба Concurrent)
        await _actionService.PressKeyWithModifiersAsync(Key.F12, ModifierKeys.Control, ct)
            .ConfigureAwait(false);

        // Ждём завершения оверлея (2000 мс) + запас
        await Task.Delay(2500, ct).ConfigureAwait(false);

        _configService.Current.Profiles.Remove(testProfile);
        await _logger.LogInfoAsync(Component,
            "[A/4] Тест A завершён. Ожидаемый результат: " +
            "'opera-оверлей' и 'opera-вкладка' стартовали В ОДНОМ timestamp (Task.WhenAll), " +
            "Opera открыла новую вкладку Ctrl+T.")
            .ConfigureAwait(false);
    }

    // ── СЦЕНАРИЙ B: строгая очередь StrictQueue x3 ────────────────────────────
    // Три EnqueueMacro подряд на один регион:
    //   Q-1 стартует немедленно, Q-2 и Q-3 ставятся в очередь.
    //   Q-2 запускается только после перехода Q-1 в Success.
    //   Q-3 запускается только после перехода Q-2 в Success.

    private async Task RunStrictQueueTestAsync(CancellationToken ct)
    {
        await _logger.LogInfoAsync(Component,
            "[B] === Тест последовательной очереди (StrictQueue x3, DelayNode 600 мс) ===")
            .ConfigureAwait(false);

        // Один регион — три DelayNode без связей.
        // NodeEngine запускается от startNode.Id; OnSuccessNodeId=null → только startNode.
        var qRegion = new ProfileRegion
        {
            RegionName    = "Q-тест",
            ExecutionMode = "StrictQueue",
            IsDirect      = false
        };

        var q1 = new DelayNode { Name = "Q-1", DelayMilliseconds = 600 };
        var q2 = new DelayNode { Name = "Q-2", DelayMilliseconds = 600 };
        var q3 = new DelayNode { Name = "Q-3", DelayMilliseconds = 600 };

        var m1 = new MacroEntry { Name = "Q-1", IsEnabled = true };
        var m2 = new MacroEntry { Name = "Q-2", IsEnabled = true };
        var m3 = new MacroEntry { Name = "Q-3", IsEnabled = true };
        m1.VisualNodes.Add(new VisualNode(q1, 100, 100));
        m2.VisualNodes.Add(new VisualNode(q2, 300, 100));
        m3.VisualNodes.Add(new VisualNode(q3, 500, 100));
        qRegion.Macros.Add(m1);
        qRegion.Macros.Add(m2);
        qRegion.Macros.Add(m3);

        await _logger.LogInfoAsync(Component,
            "[B/1] EnqueueMacro(Q-1) — стартует немедленно; " +
            "EnqueueMacro(Q-2), EnqueueMacro(Q-3) — ставятся в очередь региона 'Q-тест'.")
            .ConfigureAwait(false);

        // Три синхронных вызова: первый захватывает _running, остальные уходят в queue
        _scheduler.EnqueueMacro(qRegion, m1, q1);
        _scheduler.EnqueueMacro(qRegion, m2, q2);
        _scheduler.EnqueueMacro(qRegion, m3, q3);

        // 3 × 600 мс = 1800 мс + запас
        await Task.Delay(2500, ct).ConfigureAwait(false);

        await _logger.LogInfoAsync(Component,
            "[B/2] Тест B завершён. " +
            "Ожидаемый порядок в логах: [Q-тест] Q-1 завершён → Q-2 запущен → Q-2 завершён → Q-3 запущен → Q-3 завершён.")
            .ConfigureAwait(false);
    }

    // ── Вспомогательные методы ────────────────────────────────────────────────

    private static ProfileRegion MakeConcurrentRegion(string regionName, BaseNode node)
    {
        var region = new ProfileRegion
        {
            RegionName    = regionName,
            ExecutionMode = "Concurrent",
            IsDirect      = true
        };
        var macro = new MacroEntry { Name = node.Name, IsEnabled = true };
        macro.VisualNodes.Add(new VisualNode(node, 100, 100));
        region.Macros.Add(macro);
        return region;
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
