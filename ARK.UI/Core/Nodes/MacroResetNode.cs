using ARK.UI.Core.Bus;

namespace ARK.UI.Core.Nodes;

/// <summary>
/// При получении Trigger In генерирует DataBusPacket типа ResetRequest.
/// NodeEngine перехватывает этот тип и вызывает ResetToDefault() у всех нод графа.
/// </summary>
public sealed class MacroResetNode : BaseNode
{
    protected override Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        var sessionId   = inputPacket?.SessionId ?? Guid.NewGuid();
        var resetPacket = DataBusPacket.Reset(sessionId);

        LogToBlackBox("[MacroResetNode] Инициирован широковещательный сброс макроса.");

        return Task.FromResult(NodeResult.Success(resetPacket));
    }
}
