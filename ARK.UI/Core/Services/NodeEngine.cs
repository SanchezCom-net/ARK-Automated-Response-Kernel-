using System.Collections.Frozen;
using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models; // MacroExecutionContext, VisualConnection
using ARK.UI.Core.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Services;

public sealed class NodeEngine : INodeEngine
{
    private readonly ILogService      _logger;
    private readonly IServiceProvider _serviceProvider;

    private FrozenDictionary<Guid, BaseNode> _nodes =
        FrozenDictionary<Guid, BaseNode>.Empty;

    private List<VisualConnection> _visualConnections = [];

    private CancellationTokenSource? _cts;
    private volatile bool _isRunning;

    public bool IsRunning => _isRunning;
    public IEnumerable<BaseNode> Nodes => _nodes.Values;
    public Action<string>? DebugSink { get; set; }

    public NodeEngine(ILogService logger, IServiceProvider serviceProvider)
    {
        _logger          = logger;
        _serviceProvider = serviceProvider;
    }

    public void RegisterNodes(IEnumerable<BaseNode> nodes)
    {
        _nodes = nodes.ToFrozenDictionary(n => n.Id);
    }

    public void RegisterConnections(IEnumerable<VisualConnection> connections)
    {
        _visualConnections = connections?.ToList() ?? [];
    }

    public Task StartAsync(Guid startNodeId, CancellationToken cancellationToken = default)
        => StartAsync(startNodeId, new MacroExecutionContext(), cancellationToken);

    public async Task StartAsync(Guid startNodeId, MacroExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            await _logger.LogWarningAsync(nameof(NodeEngine), "Движок уже запущен.");
            return;
        }

        _cts       = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isRunning = true;

        try
        {
            await _logger.LogInfoAsync(nameof(NodeEngine),
                $"Запуск параллельного графа нод с ID: {startNodeId}.");

            foreach (var node in _nodes.Values)
                node.ResetToPending();

            await RegisterTriggersAsync(startNodeId, _cts.Token).ConfigureAwait(false);

            await ExecuteBranchAsync(startNodeId, null, context, _cts.Token).ConfigureAwait(false);

            await _logger.LogInfoAsync(nameof(NodeEngine), "Граф выполнен до конца.");
        }
        catch (OperationCanceledException)
        {
            await _logger.LogWarningAsync(nameof(NodeEngine), "Выполнение графа отменено.");
        }
        finally
        {
            _isRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// V3 изолированный запуск. Стартует строго с triggerNodeId (SpeechTriggerNode / HotkeyTriggerNode).
    /// TriggerRootNode остаётся в Pending — не выполняется, не подсвечивается.
    /// RegisterTriggersAsync не вызывается: событие уже обнаружено EventMonitor.
    /// </summary>
    public async Task StartAsync(Guid triggerNodeId, DataBusPacket? initPacket, CancellationToken ct = default)
    {
        if (_isRunning)
        {
            await _logger.LogWarningAsync(nameof(NodeEngine),
                $"[V3] Движок уже запущен, запуск от триггера {triggerNodeId} отклонён.");
            return;
        }

        _cts       = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;

        try
        {
            await _logger.LogInfoAsync(nameof(NodeEngine),
                $"[V3] Изолированный запуск от ноды-триггера: {triggerNodeId}. " +
                $"TriggerRootNode остаётся в Pending.").ConfigureAwait(false);

            // Сбрасываем все ноды кроме TriggerRootNode — она остаётся Pending
            foreach (var node in _nodes.Values)
            {
                if (node is not TriggerRootNode)
                    node.ResetToPending();
            }

            // Строим контекст исполнения из данных initPacket
            var context = new MacroExecutionContext();
            context.Variables["IsInteractiveTest"] = true;
            if (initPacket?.Metadata.TryGetValue("SpeechRecognizedText", out var speech) == true
                && !string.IsNullOrEmpty(speech))
                context.Variables["SpeechRecognizedText"] = speech;

            // Запускаем строго с триггерной ноды — без RegisterTriggersAsync
            await ExecuteBranchAsync(triggerNodeId, initPacket, context, _cts.Token).ConfigureAwait(false);

            await _logger.LogInfoAsync(nameof(NodeEngine),
                "[V3] Граф выполнен до конца.").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _logger.LogWarningAsync(nameof(NodeEngine),
                "[V3] Выполнение графа отменено.").ConfigureAwait(false);
        }
        finally
        {
            _isRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    // ── Fork/Join рекурсивный обход ────────────────────────────────────────

    private async Task ExecuteBranchAsync(Guid nodeId, DataBusPacket? inputPacket, MacroExecutionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_nodes.TryGetValue(nodeId, out var node)) return;

        await _logger.LogInfoAsync(nameof(NodeEngine),
            $"→ Выполняется нода '{node.Name}' [{node.GetType().Name}]")
            .ConfigureAwait(false);
        DebugSink?.Invoke($"▶ «{node.Name}» [{node.GetType().Name}]");

        bool isGate = node is Logic_QueueBlockNode;
        if (!isGate) context.BeginNode();

        var result  = await ExecuteNodeWithWatchdogAsync(node, inputPacket, context, ct).ConfigureAwait(false);
        bool success = result.IsSuccess;

        // ── Macro Reset Protocol: перехватываем ResetRequest от MacroResetNode ──
        if (result.OutputPacket?.Type == PortDataType.ResetRequest)
        {
            await _logger.LogInfoAsync(nameof(NodeEngine),
                $"[RESET] Получен сигнал сброса от ноды '{node.Name}'. Выполняю ResetToDefault() для всех нод графа.")
                .ConfigureAwait(false);
            ResetAllNodes();
        }

        if (!isGate) context.EndNode();
        if (!isGate && success) context.IncrementExecutedCount();

        await _logger.LogInfoAsync(nameof(NodeEngine),
            success
                ? $"✓ Нода '{node.Name}' завершена: Success"
                : $"✗ Нода '{node.Name}' завершена: Failed")
            .ConfigureAwait(false);
        DebugSink?.Invoke(success ? "✓ Статус: Успех" : "✗ Статус: Ошибка");

        if (success && node.LastOutputValue is not null)
            DebugSink?.Invoke(
                $"[ВЫХОД] Нода '{node.Name}' выдала в порт: \"{node.LastOutputValue}\" " +
                $"(тип: {node.LastOutputValue.GetType().Name})");

        // ── Специальная обработка: очередь последовательных шагов ────────────
        if (success && node is Logic_SequenceNode sequencer)
            await ExecuteSequencerStepsAsync(sequencer, context, ct).ConfigureAwait(false);

        // ── V3 Dual-Mode: маршрутизация BlackBox Log порта ──────────────────
        // Если нода писала в BlackBox (есть LastBlackBoxMessage) и от неё протянут
        // IsBlackBoxRoute-провод — создаём Text-пакет, кладём лог-текст в DataBus
        // и запускаем ноду-приёмник (например, OverlayTextNode для UI-отладки).
        if (!string.IsNullOrEmpty(node.LastBlackBoxMessage))
        {
            var bbWires = _visualConnections
                .Where(c => c.SourceNodeId == nodeId && c.IsBlackBoxRoute)
                .ToList();

            if (bbWires.Count > 0)
            {
                var bbSid  = inputPacket?.SessionId ?? Guid.NewGuid();
                var bbPkt  = DataBusPacket.Text(bbSid, node.Id);
                var dataBus = _serviceProvider.GetService<IDataBus>();
                dataBus?.Set(bbPkt.SessionId, bbPkt.DataId, node.LastBlackBoxMessage);

                foreach (var wire in bbWires)
                {
                    ct.ThrowIfCancellationRequested();
                    await ExecuteBranchAsync(wire.TargetNodeId, bbPkt, context, ct).ConfigureAwait(false);
                }
            }
        }

        // ── Сбор всех следующих нод ────────────────────────────────────────
        var nextIds = GetConnectedNodeIds(nodeId, isSuccessRoute: success);
        if (nextIds.Count == 0) return;

        // OutputPacket источника — передаётся только туда, куда физически подключён серебряный провод.
        // Жёлтый (Signal) провод передаёт ТОЛЬКО право на выполнение (inputPacket = null для приёмника).
        var childPacket = result.OutputPacket;

        // ── Разделение на шлюзы и обычные ноды ────────────────────────────
        // Полные шлюзы (WaitFullChain=true)  — запускаются ПОСЛЕ Task.WhenAll параллельных.
        // Частичные шлюзы (WaitFullChain=false) — запускаются когда WaitNodesCount задач завершено.
        // Обычные ноды — параллельный Fork, как прежде.
        List<Guid> fullGateIds    = [];
        List<Guid> partialGateIds = [];
        List<Guid> regularIds     = [];

        foreach (var id in nextIds)
        {
            if (_nodes.TryGetValue(id, out var nextNode) && nextNode is Logic_QueueBlockNode qbNext)
            {
                if (qbNext.WaitFullChain) fullGateIds.Add(id);
                else                      partialGateIds.Add(id);
            }
            else
            {
                regularIds.Add(id);
            }
        }

        // ── Приоритет: строго последовательный порядок обычных нод ──────────
        bool hasExplicitPriority = regularIds.Any(id =>
            _nodes.TryGetValue(id, out var n) && n.QueuePriority != 0);

        if (hasExplicitPriority)
        {
            var ordered = regularIds
                .Select(id => (id, pri: _nodes.TryGetValue(id, out var n) ? n.QueuePriority : 0))
                .OrderBy(x => x.pri == 0 ? int.MaxValue : x.pri)
                .Select(x => x.id);

            foreach (var nextId in ordered)
                await ExecuteBranchAsync(nextId, ResolveInputPacket(nodeId, nextId, childPacket), context, ct).ConfigureAwait(false);

            // Шлюзы после приоритетных нод — последовательно
            foreach (var gateId in fullGateIds.Concat(partialGateIds))
                await ExecuteBranchAsync(gateId, ResolveInputPacket(nodeId, gateId, childPacket), context, ct).ConfigureAwait(false);

            return;
        }

        // Снимок счётчика перед запуском параллельных задач —
        // нужен частичным шлюзам для подсчёта «N нод выполнено на этом шаге».
        int executionStartCount = context.ExecutedNodesCount;

        // ── Fork обычных нод ────────────────────────────────────────────────
        Task[]? parallelTasks = regularIds.Count > 0
            ? regularIds
                .Select(id => Task.Run(() => ExecuteBranchAsync(id, ResolveInputPacket(nodeId, id, childPacket), context, ct), ct))
                .ToArray()
            : null;

        // ── Полные шлюзы: дождаться ВСЕХ параллельных задач, затем последовательно ──
        foreach (var gateId in fullGateIds)
        {
            if (parallelTasks is not null)
            {
                await Task.WhenAll(parallelTasks).ConfigureAwait(false);
                parallelTasks = null;
            }
            await ExecuteBranchAsync(gateId, ResolveInputPacket(nodeId, gateId, childPacket), context, ct).ConfigureAwait(false);
        }

        // ── Частичные шлюзы: polling по ExecutedNodesCount ──────────────────
        // Ждём пока WaitNodesCount нод завершатся с момента запуска этой ветки.
        // Fallback: если все параллельные задачи завершились раньше — не ждём бесконечно.
        foreach (var gateId in partialGateIds)
        {
            if (_nodes.TryGetValue(gateId, out var gateNode) && gateNode is Logic_QueueBlockNode qbGate)
            {
                int waitCount = Math.Max(1, qbGate.WaitNodesCount);

                if (parallelTasks is { Length: > 0 })
                {
                    var allDoneTask = Task.WhenAll(parallelTasks);

                    while (context.ExecutedNodesCount - executionStartCount < waitCount
                           && !allDoneTask.IsCompleted)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(50, ct).ConfigureAwait(false);
                    }

                    await ExecuteBranchAsync(gateId, ResolveInputPacket(nodeId, gateId, childPacket), context, ct).ConfigureAwait(false);
                    await allDoneTask.ConfigureAwait(false);
                    parallelTasks = null;
                }
                else
                {
                    await ExecuteBranchAsync(gateId, ResolveInputPacket(nodeId, gateId, childPacket), context, ct).ConfigureAwait(false);
                }
            }
        }

        // Финальный Join: если шлюзов не было или parallelTasks не был ожидаем выше
        if (parallelTasks is not null)
            await Task.WhenAll(parallelTasks).ConfigureAwait(false);
    }

    // ── Последовательное выполнение шагов Logic_SequenceNode ─────────────

    private async Task ExecuteSequencerStepsAsync(
        Logic_SequenceNode sequencer, MacroExecutionContext context, CancellationToken ct)
    {
        foreach (var step in sequencer.Steps)
        {
            ct.ThrowIfCancellationRequested();

            var stepConns = _visualConnections
                .Where(c => c.SourceNodeId == sequencer.Id && c.StepId == step.StepId)
                .ToList();

            if (stepConns.Count == 0) continue;

            DebugSink?.Invoke($"  [ШАГ] «{step.Name}»");
            await _logger.LogInfoAsync(nameof(NodeEngine),
                $"[ОЧЕРЕДЬ] Шаг «{step.Name}»").ConfigureAwait(false);

            if (sequencer.LastOutputValue is not null)
            {
                var dataConn = stepConns.FirstOrDefault(c => c.IsDataRoute);
                if (dataConn is not null)
                    context.Variables[$"Var_{sequencer.Id}"] = sequencer.LastOutputValue;
            }

            var successConn = stepConns.FirstOrDefault(c => !c.IsErrorRoute && !c.IsDataRoute);
            if (successConn is not null)
            {
                // Sequencer-шаги: null-пакет (данные идут через V2 context.Variables)
                await ExecuteBranchAsync(successConn.TargetNodeId, null, context, ct).ConfigureAwait(false);
            }
            else
            {
                var errorConn = stepConns.FirstOrDefault(c => c.IsErrorRoute);
                if (errorConn is not null)
                    await ExecuteBranchAsync(errorConn.TargetNodeId, null, context, ct).ConfigureAwait(false);
            }
        }
    }

    // ── V3 Event-Driven: регистрация триггеров ────────────────────────────

    /// <summary>
    /// Находит все ноды-триггеры, подключённые к startNodeId через success-провода,
    /// выставляет им IsListening = true и вызывает OnStartListeningAsync.
    /// Ноды, не подключённые к стартовой, остаются IsListening = false (обесточены).
    /// </summary>
    public async Task RegisterTriggersAsync(Guid startNodeId, CancellationToken ct = default)
    {
        var triggerIds = _visualConnections
            .Where(c => c.SourceNodeId == startNodeId && !c.IsDataRoute && !c.IsErrorRoute)
            .Select(c => c.TargetNodeId)
            .Distinct();

        foreach (var id in triggerIds)
        {
            if (!_nodes.TryGetValue(id, out var node)) continue;

            node.IsListening = true;
            node.DebugSink   = DebugSink;
            await node.OnStartListeningAsync(ct).ConfigureAwait(false);
            DebugSink?.Invoke($"[TRIGGER REGISTER] «{node.Name}» → IsListening=true");
        }
    }

    // ── V3 Watchdog: обёртка выполнения ноды с таймаутом ─────────────────

    // §2.5.1.1: NodeTimeoutMs = 0 → бесконечность (Watchdog не срабатывает по таймауту).
    // §CSP 1.2.3.2: превышение Soft Timeout критической секции → NodeState.ERROR
    //               + BlackBox: "Critical Section Soft Timeout exceeded: Context cascade canceled".
    // §HEARTBEAT 1.3: обычный Watchdog timeout (не Critical Section) → NodeState.ZOMBIE.
    private async Task<NodeResult> ExecuteNodeWithWatchdogAsync(
        BaseNode node,
        DataBusPacket? packet,
        MacroExecutionContext context,
        CancellationToken parentCt)
    {
        node.DebugSink = DebugSink;
        node.SetLegacyContext(context);

        // §2.5.1.1: 0 = бесконечность — Watchdog по таймауту не срабатывает
        int timeoutMs = node.NodeTimeoutMs;

        var blackBox = _serviceProvider.GetService<IBlackBoxLogger>();
        var dataBus  = _serviceProvider.GetService<IDataBus>();

        // CSP (Critical Section Protocol): ноды с IsCriticalSection=true получают
        // изолированный токен — глобальный Stop() не отменяет их мгновенно.
        // Для обычных нод: стандартная связка с parentCt (мгновенная отмена).
        using var nodeCts = node.IsCriticalSection
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(parentCt);

        var task = node.ExecuteAsync(_serviceProvider, _logger, blackBox, dataBus, packet, nodeCts.Token);

        while (!task.IsCompleted)
        {
            try { await Task.Delay(500, parentCt).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // parentCt сработал (пользователь нажал Stop)
                if (node.IsCriticalSection && !task.IsCompleted)
                {
                    // CSP Soft Abort: критическая секция получает 5 сек на graceful exit
                    node.SetState(NodeState.Waiting);
                    blackBox?.Log(node.Id,
                        $"[CSP SOFT ABORT] Нода '{node.Name}': критическая секция, " +
                        "ожидаю самостоятельного завершения (5 сек)...");
                    await _logger.LogWarningAsync(nameof(NodeEngine),
                        $"[CSP] '{node.Name}' — глобальный Stop, Soft Timeout 5 сек.")
                        .ConfigureAwait(false);

                    await Task.WhenAny(task, Task.Delay(5_000)).ConfigureAwait(false);

                    if (!task.IsCompleted)
                    {
                        // §CSP 1.2.3.2: Soft Timeout exceeded → NodeState.Error (не Zombie!)
                        node.SetState(NodeState.Error);
                        blackBox?.Log(node.Id,
                            "Critical Section Soft Timeout exceeded: Context cascade canceled");
                        await _logger.LogErrorAsync(nameof(NodeEngine),
                            $"[CSP] '{node.Name}' не завершилась за 5 сек — Error.", null)
                            .ConfigureAwait(false);
                        nodeCts.Cancel();
                        try { await task.ConfigureAwait(false); } catch { }
                        return NodeResult.Failure(
                            "Critical Section Soft Timeout exceeded: Context cascade canceled");
                    }
                }
                break;
            }

            // §2.5.1.1: 0 = бесконечность — пропускаем проверку таймаута
            if (timeoutMs == 0 || node.WatchdogElapsed.TotalMilliseconds <= timeoutMs) continue;

            // ── Watchdog-таймаут: нода зависла без вызова ResetWatchdogTimer() ──
            if (node.IsCriticalSection)
            {
                // Критическая секция: даём ещё 5 сек (Soft Timeout)
                node.SetState(NodeState.Waiting);
                blackBox?.Log(node.Id,
                    $"[WATCHDOG SOFT] Нода '{node.Name}' превысила {timeoutMs}мс. " +
                    "Критическая секция — ожидаю ещё 5 сек.");
                try { await Task.WhenAny(task, Task.Delay(5_000, parentCt)).ConfigureAwait(false); }
                catch (OperationCanceledException) { }

                if (task.IsCompleted) break;

                // §CSP 1.2.3.2: Soft Timeout exceeded → NodeState.Error (не Zombie!)
                node.SetState(NodeState.Error);
                blackBox?.Log(node.Id,
                    "Critical Section Soft Timeout exceeded: Context cascade canceled");
                await _logger.LogErrorAsync(nameof(NodeEngine),
                    $"[WATCHDOG CSP] '{node.Name}': Critical Section Soft Timeout exceeded.", null)
                    .ConfigureAwait(false);
            }
            else
            {
                // §HEARTBEAT 1.3: обычная нода → ZOMBIE (не отвечала на Watchdog/Pong)
                node.SetState(NodeState.Zombie);
                blackBox?.Log(node.Id, $"[WATCHDOG ZOMBIE] Нода '{node.Name}' убита по таймауту {timeoutMs}мс.");
                await _logger.LogErrorAsync(nameof(NodeEngine),
                    $"[WATCHDOG] Нода '{node.Name}' [{node.Id}] превысила таймаут {timeoutMs}мс — ZOMBIE, принудительная отмена.", null)
                    .ConfigureAwait(false);
            }

            nodeCts.Cancel();

            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { }

            return node.IsCriticalSection
                ? NodeResult.Failure("Critical Section Soft Timeout exceeded: Context cascade canceled")
                : NodeResult.Failure($"[WATCHDOG] Таймаут {timeoutMs}мс истёк для ноды '{node.Name}'.");
        }

        return await task.ConfigureAwait(false);
    }

    // ── Macro Reset Protocol ──────────────────────────────────────────────

    private void ResetAllNodes()
    {
        foreach (var node in _nodes.Values)
            node.ResetToDefault();
    }

    // ── Wire-type resolution: данные идут только по серебряному проводу ────

    /// <summary>
    /// Возвращает <paramref name="sourcePacket"/> только если от <paramref name="sourceNodeId"/>
    /// к <paramref name="targetNodeId"/> проведён серебряный провод (IsDataRoute = true).
    /// Жёлтый (Signal) провод передаёт исключительно право на выполнение — данные не передаются.
    /// </summary>
    private DataBusPacket? ResolveInputPacket(Guid sourceNodeId, Guid targetNodeId, DataBusPacket? sourcePacket)
    {
        if (sourcePacket is null) return null;
        return _visualConnections.Any(c => c.SourceNodeId == sourceNodeId
                                           && c.TargetNodeId == targetNodeId
                                           && c.IsDataRoute)
            ? sourcePacket
            : null;
    }

    // ── Определение исходящих нод по визуальным связям (с fallback) ──────

    private List<Guid> GetConnectedNodeIds(Guid nodeId, bool isSuccessRoute)
    {
        // Сканируем все нарисованные провода: SourceNodeId == nodeId,
        // не Data-провод, IsErrorRoute соответствует маршруту.
        var result = _visualConnections
            .Where(c => c.SourceNodeId == nodeId
                        && !c.IsDataRoute
                        && !c.IsBlackBoxRoute   // BlackBox-провода обрабатываются отдельно
                        && c.IsErrorRoute == !isSuccessRoute)
            .Select(c => c.TargetNodeId)
            .Distinct()
            .ToList();

        // Серебряный провод как неявный маршрут успеха:
        // если жёлтых Success-связей нет — нода данных получает поток выполнения.
        if (isSuccessRoute && result.Count == 0)
        {
            var dataIds = _visualConnections
                .Where(c => c.SourceNodeId == nodeId && c.IsDataRoute)
                .Select(c => c.TargetNodeId)
                .Distinct();
            result.AddRange(dataIds);
        }

        // Fallback: визуальных связей нет → берём логическое свойство ноды
        if (result.Count == 0 && _nodes.TryGetValue(nodeId, out var node))
        {
            var fallbackId = isSuccessRoute ? node.OnSuccessNodeId : node.OnErrorNodeId;
            if (fallbackId.HasValue) result.Add(fallbackId.Value);
        }

        return result;
    }

    public void Stop() => _cts?.Cancel();
}
