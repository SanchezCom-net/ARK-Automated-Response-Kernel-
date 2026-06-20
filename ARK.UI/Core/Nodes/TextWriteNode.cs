using ARK.UI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class TextWriteNode : BaseNode
{
    public override string DefaultDataInputPropertyName => nameof(Text);

    public string Text { get; set; } = string.Empty;

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        TryApplyContextInput<string>(nameof(Text), v => Text = v);

        var actionService = serviceProvider.GetRequiredService<IActionService>();
        await actionService.TypeTextAsync(Text, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
