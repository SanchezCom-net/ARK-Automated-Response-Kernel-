using ARK.UI.Core.Bus;

namespace ARK.UI.Core.Interfaces;

/// <summary>
/// Глобальный мост (The Bridge): принимает Guid макроса и маршрутизирует
/// его запуск — через региональную очередь или напрямую через NodeEngine.
/// </summary>
public interface IMacroOrchestrator
{
    /// <summary>
    /// Поставить макрос в очередь (если у него RegionId) или запустить немедленно.
    /// Читает политику из <see cref="Core.Nodes.MacroPolicyNode"/> внутри графа.
    /// </summary>
    /// <param name="macroId">ID макроса.</param>
    /// <param name="triggerNodeId">
    /// ID конкретной ноды-триггера (SpeechTriggerNode / HotkeyTriggerNode), от которой стартует
    /// выполнение. NodeEngine пропускает TriggerRootNode и начинает строго с этой ноды.
    /// Guid.Empty / default = устаревший путь (старт с StartNodeId макроса).
    /// </param>
    /// <param name="inputPacket">
    /// Опциональный пакет данных от триггера (распознанная речь в <c>Metadata["SpeechRecognizedText"]</c>).
    /// </param>
    Task EnqueueMacroAsync(Guid macroId, Guid triggerNodeId = default, DataBusPacket? inputPacket = null, CancellationToken ct = default);
}
