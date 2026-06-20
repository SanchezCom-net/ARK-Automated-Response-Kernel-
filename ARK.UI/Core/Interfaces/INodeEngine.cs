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
    Task StartAsync(Guid startNodeId, CancellationToken cancellationToken = default);
    Task StartAsync(Guid startNodeId, MacroExecutionContext context, CancellationToken cancellationToken = default);
    void Stop();
}
