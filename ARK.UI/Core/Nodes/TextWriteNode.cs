using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class TextWriteNode : BaseNode
{
    public override string DefaultDataInputPropertyName => nameof(Text);

    public string Text { get; set; } = string.Empty;

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _raw))
        {
            var _s = _raw as string ?? _raw?.ToString();
            if (_s is not null) Text = _s;
        }

        var actionService = NodeServices!.GetRequiredService<IActionService>();
        await actionService.TypeTextAsync(Text, ct).ConfigureAwait(false);
        return NodeResult.Success(null);
    }
}
