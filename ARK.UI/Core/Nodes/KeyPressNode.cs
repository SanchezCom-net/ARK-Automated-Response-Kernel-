using System.Windows.Input;
using ARK.UI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class KeyPressNode : BaseNode
{
    public Key          Key       { get; set; } = Key.None;
    public ModifierKeys Modifiers { get; set; } = ModifierKeys.None;

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        var actionService = serviceProvider.GetRequiredService<IActionService>();

        if (Modifiers == ModifierKeys.None)
            await actionService.PressKeyAsync(Key, cancellationToken).ConfigureAwait(false);
        else
            await actionService.PressKeyWithModifiersAsync(Key, Modifiers, cancellationToken)
                .ConfigureAwait(false);

        return true;
    }
}
