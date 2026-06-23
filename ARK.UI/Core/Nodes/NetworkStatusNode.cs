using System.Text.Json;
using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class NetworkStatusNode : BaseNode
{
    public string StatusMessage { get; set; } = string.Empty;

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        var network = NodeServices!.GetRequiredService<INetworkService>();

        if (!network.IsConnected)
        {
            await NodeLogger!.LogInfoAsync(Name, "WebSocket не подключён, отправка пропущена.")
                .ConfigureAwait(false);
            return NodeResult.Success(null);
        }

        var payload = JsonSerializer.Serialize(new
        {
            NodeId   = Id.ToString(),
            NodeName = Name,
            Status   = StatusMessage
        });

        await network.SendAsync(payload, ct).ConfigureAwait(false);
        await NodeLogger!.LogInfoAsync(Name, $"Статус отправлен: {StatusMessage}").ConfigureAwait(false);
        return NodeResult.Success(null);
    }
}
