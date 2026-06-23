using System.Collections.Concurrent;
using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Services;

/// <summary>
/// Глобальный мост между триггерами и исполнением макросов.
/// Singleton. Реализует политику Self-Collision (Parallel / Drop / Queue / Restart).
/// Маршрутизирует в IQueueManager (регион) или запускает NodeEngine напрямую (глобальный).
/// Использует IActiveDocumentRegistry: если макрос открыт в редакторе —
/// движок выполняется на тех же нодах что отображены на канвасе (→ анимации State).
/// </summary>
public sealed class MacroOrchestrator : IMacroOrchestrator
{
    private const string Component = nameof(MacroOrchestrator);

    private readonly IStorageManager          _storage;
    private readonly IQueueManager            _queue;
    private readonly IServiceProvider         _services;
    private readonly ILogService              _logger;
    private readonly IActiveDocumentRegistry  _activeDocRegistry;

    // ── Self-Collision tracking ──────────────────────────────────────────────

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeExecutions = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim>           _privateQueues    = new();

    public MacroOrchestrator(
        IStorageManager         storage,
        IQueueManager           queue,
        IServiceProvider        services,
        ILogService             logger,
        IActiveDocumentRegistry activeDocRegistry)
    {
        _storage           = storage;
        _queue             = queue;
        _services          = services;
        _logger            = logger;
        _activeDocRegistry = activeDocRegistry;
    }

    public async Task EnqueueMacroAsync(
        Guid macroId,
        Guid triggerNodeId  = default,
        DataBusPacket? inputPacket = null,
        CancellationToken ct = default)
    {
        MacroDocument doc;
        try
        {
            doc = await _storage.LoadMacroAsync(macroId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(Component,
                $"Макрос {macroId} не найден в хранилище.", ex).ConfigureAwait(false);
            return;
        }

        var policy = doc.Macro.VisualNodes
            .Select(vn => vn.LogicalNode)
            .OfType<MacroPolicyNode>()
            .FirstOrDefault();

        int  priority = policy?.PriorityLevel  ?? 0;
        bool monopoly = policy?.MonopolyMode   ?? false;
        bool immunity = policy?.SystemImmunity ?? false;

        var triggerRoot = doc.Macro.VisualNodes
            .Select(vn => vn.LogicalNode)
            .OfType<TriggerRootNode>()
            .FirstOrDefault();

        var strategy = triggerRoot?.CollisionStrategy ?? SelfCollisionStrategy.Parallel;

        await _logger.LogInfoAsync(Component,
            $"[Orchestrator] '{doc.UserDefinedName}' triggerNode={triggerNodeId} " +
            $"priority={priority} strategy={strategy} monopoly={monopoly}")
            .ConfigureAwait(false);

        var regionId = doc.RegionId ?? doc.Macro.RegionId;
        if (regionId is not null)
        {
            await _queue.EnqueueAsync(macroId, triggerNodeId, regionId.Value, priority, ct)
                .ConfigureAwait(false);
            return;
        }

        await ApplyCollisionStrategyAsync(doc, triggerNodeId, inputPacket, strategy, ct)
            .ConfigureAwait(false);
    }

    // ── Self-Collision routing ───────────────────────────────────────────────

    private async Task ApplyCollisionStrategyAsync(
        MacroDocument  doc,
        Guid           triggerNodeId,
        DataBusPacket? inputPacket,
        SelfCollisionStrategy strategy,
        CancellationToken ct)
    {
        bool isRunning = _activeExecutions.ContainsKey(doc.Id);

        if (!isRunning)
        {
            await RunDirectAsync(doc, triggerNodeId, inputPacket, CancellationToken.None, ct)
                .ConfigureAwait(false);
            return;
        }

        switch (strategy)
        {
            case SelfCollisionStrategy.Drop:
                await _logger.LogInfoAsync(Component,
                    $"[Collision:Drop] Макрос '{doc.UserDefinedName}' уже выполняется. " +
                    $"Новый триггер отброшен.").ConfigureAwait(false);
                return;

            case SelfCollisionStrategy.Restart:
                await HandleRestartAsync(doc, triggerNodeId, inputPacket, ct).ConfigureAwait(false);
                return;

            case SelfCollisionStrategy.Queue:
                await HandleQueueAsync(doc, triggerNodeId, inputPacket, ct).ConfigureAwait(false);
                return;

            default: // Parallel
                await RunDirectAsync(doc, triggerNodeId, inputPacket, CancellationToken.None, ct)
                    .ConfigureAwait(false);
                return;
        }
    }

    private async Task HandleRestartAsync(
        MacroDocument  doc,
        Guid           triggerNodeId,
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        await _logger.LogInfoAsync(Component,
            $"[Collision:Restart] Отменяем текущее выполнение '{doc.UserDefinedName}'...")
            .ConfigureAwait(false);

        if (_activeExecutions.TryGetValue(doc.Id, out var existingCts))
        {
            try { existingCts.Cancel(); } catch (ObjectDisposedException) { }
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_activeExecutions.ContainsKey(doc.Id) && DateTime.UtcNow < deadline)
            await Task.Delay(20, ct).ConfigureAwait(false);

        if (_activeExecutions.ContainsKey(doc.Id))
        {
            await _logger.LogWarningAsync(Component,
                $"[Collision:Restart] '{doc.UserDefinedName}' не завершился за 10 сек. " +
                $"Принудительно продолжаем.").ConfigureAwait(false);
        }

        await RunDirectAsync(doc, triggerNodeId, inputPacket, CancellationToken.None, ct)
            .ConfigureAwait(false);
    }

    private async Task HandleQueueAsync(
        MacroDocument  doc,
        Guid           triggerNodeId,
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        var sem = _privateQueues.GetOrAdd(doc.Id, _ => new SemaphoreSlim(1, 1));

        if (!await sem.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await _logger.LogInfoAsync(Component,
                $"[Collision:Queue] Очередь '{doc.UserDefinedName}' занята. " +
                $"Дополнительный вызов отброшен.").ConfigureAwait(false);
            return;
        }

        await _logger.LogInfoAsync(Component,
            $"[Collision:Queue] '{doc.UserDefinedName}' ожидает завершения текущего выполнения...")
            .ConfigureAwait(false);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (_activeExecutions.ContainsKey(doc.Id) && DateTime.UtcNow < deadline)
                await Task.Delay(50, ct).ConfigureAwait(false);

            await RunDirectAsync(doc, triggerNodeId, inputPacket, CancellationToken.None, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    // ── Немедленный запуск ───────────────────────────────────────────────────

    private async Task RunDirectAsync(
        MacroDocument  doc,
        Guid           triggerNodeId,
        DataBusPacket? inputPacket,
        CancellationToken engineCt,
        CancellationToken externalCt = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(engineCt, externalCt);

        _activeExecutions.TryAdd(doc.Id, cts);

        try
        {
            var engine = _services.GetRequiredService<INodeEngine>();

            // Если макрос открыт в редакторе — используем отображаемые ноды,
            // чтобы State-анимации на канвасе обновлялись в реальном времени.
            var active = _activeDocRegistry.GetActive(doc.Id);
            engine.RegisterNodes(
                active?.Nodes ?? doc.Macro.VisualNodes.Select(vn => vn.LogicalNode).ToList());
            engine.RegisterConnections(
                active?.Connections ?? doc.Macro.VisualConnections);

            if (triggerNodeId != Guid.Empty)
            {
                await engine.StartAsync(triggerNodeId, inputPacket, cts.Token).ConfigureAwait(false);
            }
            else
            {
                if (doc.Macro.StartNodeId is not { } startNodeId)
                {
                    await _logger.LogErrorAsync(Component,
                        $"Макрос '{doc.UserDefinedName}' не имеет стартовой ноды.").ConfigureAwait(false);
                    return;
                }

                var context = new MacroExecutionContext();
                context.Variables["IsInteractiveTest"] = true;

                if (inputPacket?.Metadata.TryGetValue("SpeechRecognizedText", out var speechText) == true
                    && !string.IsNullOrEmpty(speechText))
                    context.Variables["SpeechRecognizedText"] = speechText;

                await engine.StartAsync(startNodeId, context, cts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _activeExecutions.TryRemove(doc.Id, out _);
        }
    }
}
