using ARK.UI.Core.Bus;
using ARK.UI.Core.Models;
using ARK.UI.Core.Nodes;

namespace ARK.UI.Core.Interfaces;

public interface INodeEngine
{
    bool IsRunning { get; }
    IEnumerable<BaseNode> Nodes { get; }
    Action<string>? DebugSink { get; set; }

    void RegisterNodes(IEnumerable<BaseNode> nodes);
    void RegisterConnections(IEnumerable<VisualConnection> connections);
    Task RegisterTriggersAsync(Guid startNodeId, CancellationToken ct = default);

    /// <summary>Устаревший путь: старт с произвольной ноды (DiagnosticsService, демо-ноды).</summary>
    Task StartAsync(Guid startNodeId, CancellationToken cancellationToken = default);
    /// <summary>Устаревший путь: старт с произвольной ноды + контекст.</summary>
    Task StartAsync(Guid startNodeId, MacroExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// V3 изолированный запуск: стартует строго с <paramref name="triggerNodeId"/> (SpeechTriggerNode /
    /// HotkeyTriggerNode). TriggerRootNode остаётся в состоянии Pending — не выполняется.
    /// RegisterTriggersAsync не вызывается: триггер уже обнаружен внешне (EventMonitor).
    /// </summary>
    Task StartAsync(Guid triggerNodeId, DataBusPacket? initPacket, CancellationToken ct = default);

    void Stop();
}
