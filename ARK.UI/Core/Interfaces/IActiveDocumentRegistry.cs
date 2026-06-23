using ARK.UI.Core.Models;
using ARK.UI.Core.Nodes;

namespace ARK.UI.Core.Interfaces;

/// <summary>
/// Хранит ссылки на ноды макроса, открытого в редакторе BlueprintEditor.
/// MacroOrchestrator и QueueManager используют эти ссылки вместо загрузки
/// свежей копии с диска, чтобы изменения NodeState.Executing/Success/Failed
/// отображались на канвасе в реальном времени.
/// </summary>
public interface IActiveDocumentRegistry
{
    /// <summary>Регистрирует ноды открытого документа при загрузке в редактор.</summary>
    void Register(Guid macroId, IReadOnlyList<BaseNode> nodes, IReadOnlyList<VisualConnection> connections);

    /// <summary>Снимает регистрацию при закрытии редактора или переходе к другому макросу.</summary>
    void Unregister(Guid macroId);

    /// <summary>
    /// Возвращает зарегистрированные ноды/провода, если документ открыт в редакторе.
    /// null — документ не открыт, движок должен загрузить свежую копию с диска.
    /// </summary>
    (IReadOnlyList<BaseNode> Nodes, IReadOnlyList<VisualConnection> Connections)? GetActive(Guid macroId);
}
