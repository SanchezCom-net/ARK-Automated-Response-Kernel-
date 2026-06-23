using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class MouseClickNode : BaseNode
{
    public double X { get; set; }
    public double Y { get; set; }

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        var actionService = NodeServices!.GetRequiredService<IActionService>();
        await actionService.ClickAsync(X, Y, ct).ConfigureAwait(false);
        return NodeResult.Success(null);
    }
}
