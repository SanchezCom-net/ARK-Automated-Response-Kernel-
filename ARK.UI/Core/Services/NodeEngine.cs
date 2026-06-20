using System.Collections.Frozen;
using System.Reflection;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Nodes;

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

            await ExecuteBranchAsync(startNodeId, context, _cts.Token).ConfigureAwait(false);

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

    // ── Fork/Join рекурсивный обход ────────────────────────────────────────

    private async Task ExecuteBranchAsync(Guid nodeId, MacroExecutionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_nodes.TryGetValue(nodeId, out var node)) return;

        await _logger.LogInfoAsync(nameof(NodeEngine),
            $"→ Выполняется нода '{node.Name}' [{node.GetType().Name}]")
            .ConfigureAwait(false);
        DebugSink?.Invoke($"▶ «{node.Name}» [{node.GetType().Name}]");

        bool isGate = node is Logic_QueueBlockNode;
        if (!isGate) context.BeginNode();

        // Прокидываем sink → TryApplyContextInput сможет логировать ВХОД/ПРЕДУПРЕЖДЕНИЕ
        node.DebugSink = DebugSink;

        bool success = await node
            .ExecuteAsync(_serviceProvider, _logger, context, ct)
            .ConfigureAwait(false);

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

        // ── Данные по серебряным проводам (до запуска следующих веток) ──────
        // Запись в шину происходит ТОЛЬКО при наличии физического серебряного провода
        // (IsDataRoute == true). Жёлтые/красные провода не инициируют запись In:{id}:{prop}.
        if (success && node.IsDataOutputEnabled && node.LastOutputValue is not null)
        {
            var dataWires = _visualConnections
                .Where(c => c.SourceNodeId == node.Id && c.IsDataRoute)
                .ToList();

            if (dataWires.Count > 0)
            {
                // Var_{sourceId} — сырое значение источника; пишем один раз для всех приёмников.
                context.Variables[$"Var_{node.Id}"] = node.LastOutputValue;

                foreach (var wire in dataWires)
                {
                    if (!_nodes.TryGetValue(wire.TargetNodeId, out var dataTarget)) continue;

                    string propName = dataTarget.DefaultDataInputPropertyName;
                    if (string.IsNullOrEmpty(propName)) continue;

                    // Адресная доставка: строго по целевому ID и имени свойства.
                    context.Variables[$"In:{dataTarget.Id}:{propName}"] = node.LastOutputValue;

                    var prop = dataTarget.GetType()
                        .GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                    if (prop is not null)
                    {
                        // DataPacket не устанавливается рефлексией напрямую —
                        // кастинг и распаковку выполнит TryApplyContextInput при выполнении ноды.
                        if (node.LastOutputValue is not DataPacket)
                        {
                            try { prop.SetValue(dataTarget, node.LastOutputValue); }
                            catch { /* несовместимые типы — TryApplyContextInput обработает при выполнении */ }
                        }
                        dataTarget.RaisePropertyChanged(prop.Name);
                    }

                    DebugSink?.Invoke(
                        $"[ДАННЫЕ] → «{dataTarget.Name}».{propName} = \"{node.LastOutputValue}\"");
                }
            }
        }

        // ── Сбор всех следующих нод ────────────────────────────────────────
        var nextIds = GetConnectedNodeIds(nodeId, isSuccessRoute: success);
        if (nextIds.Count == 0) return;

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
                await ExecuteBranchAsync(nextId, context, ct).ConfigureAwait(false);

            // Шлюзы после приоритетных нод — последовательно
            foreach (var gateId in fullGateIds.Concat(partialGateIds))
                await ExecuteBranchAsync(gateId, context, ct).ConfigureAwait(false);

            return;
        }

        // Снимок счётчика перед запуском параллельных задач —
        // нужен частичным шлюзам для подсчёта «N нод выполнено на этом шаге».
        int executionStartCount = context.ExecutedNodesCount;

        // ── Fork обычных нод ────────────────────────────────────────────────
        Task[]? parallelTasks = regularIds.Count > 0
            ? regularIds
                .Select(id => Task.Run(() => ExecuteBranchAsync(id, context, ct), ct))
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
            await ExecuteBranchAsync(gateId, context, ct).ConfigureAwait(false);
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

                    await ExecuteBranchAsync(gateId, context, ct).ConfigureAwait(false);
                    await allDoneTask.ConfigureAwait(false);
                    parallelTasks = null;
                }
                else
                {
                    await ExecuteBranchAsync(gateId, context, ct).ConfigureAwait(false);
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
                await ExecuteBranchAsync(successConn.TargetNodeId, context, ct).ConfigureAwait(false);
            }
            else
            {
                var errorConn = stepConns.FirstOrDefault(c => c.IsErrorRoute);
                if (errorConn is not null)
                    await ExecuteBranchAsync(errorConn.TargetNodeId, context, ct).ConfigureAwait(false);
            }
        }
    }

    // ── Определение исходящих нод по визуальным связям (с fallback) ──────

    private List<Guid> GetConnectedNodeIds(Guid nodeId, bool isSuccessRoute)
    {
        // Сканируем все нарисованные провода: SourceNodeId == nodeId,
        // не Data-провод, IsErrorRoute соответствует маршруту.
        var result = _visualConnections
            .Where(c => c.SourceNodeId == nodeId
                        && !c.IsDataRoute
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
