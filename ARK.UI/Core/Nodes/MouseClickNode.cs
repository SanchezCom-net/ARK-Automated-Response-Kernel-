using ARK.UI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class MouseClickNode : BaseNode
{
    public double X { get; set; }
    public double Y { get; set; }

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        var actionService = serviceProvider.GetRequiredService<IActionService>();
        await actionService.ClickAsync(X, Y, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
