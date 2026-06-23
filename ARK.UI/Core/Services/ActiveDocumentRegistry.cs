using System.Collections.Concurrent;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Nodes;

namespace ARK.UI.Core.Services;

/// <summary>
/// Singleton. Хранит один открытый документ на каждый macroId.
/// Потокобезопасен: MacroOrchestrator обращается из Task.Run,
/// BlueprintEditorViewModel — из UI-потока.
/// </summary>
public sealed class ActiveDocumentRegistry : IActiveDocumentRegistry
{
    private readonly ConcurrentDictionary<Guid, (IReadOnlyList<BaseNode> Nodes, IReadOnlyList<VisualConnection> Connections)>
        _docs = new();

    public void Register(Guid macroId,
        IReadOnlyList<BaseNode> nodes,
        IReadOnlyList<VisualConnection> connections)
        => _docs[macroId] = (nodes, connections);

    public void Unregister(Guid macroId) => _docs.TryRemove(macroId, out _);

    public (IReadOnlyList<BaseNode> Nodes, IReadOnlyList<VisualConnection> Connections)? GetActive(Guid macroId)
        => _docs.TryGetValue(macroId, out var entry) ? entry : null;
}
