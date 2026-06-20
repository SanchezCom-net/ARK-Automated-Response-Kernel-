using System.Text.Json;
using ARK.UI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class NetworkStatusNode : BaseNode
{
    public string StatusMessage { get; set; } = string.Empty;

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        var network = serviceProvider.GetRequiredService<INetworkService>();

        if (!network.IsConnected)
        {
            await logger.LogInfoAsync(Name, "WebSocket не подключён, отправка пропущена.")
                .ConfigureAwait(false);
            return true;
        }

        var payload = JsonSerializer.Serialize(new
        {
            NodeId   = Id.ToString(),
            NodeName = Name,
            Status   = StatusMessage
        });

        await network.SendAsync(payload, cancellationToken).ConfigureAwait(false);
        await logger.LogInfoAsync(Name, $"Статус отправлен: {StatusMessage}").ConfigureAwait(false);
        return true;
    }
}
