using System.Windows.Input;
using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class KeyPressNode : BaseNode
{
    public Key          Key       { get; set; } = Key.None;
    public ModifierKeys Modifiers { get; set; } = ModifierKeys.None;

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        var actionService = NodeServices!.GetRequiredService<IActionService>();

        // Smart Fields V3.6: метаданные шины переопределяют значение из UI
        var effectiveKey       = Key;
        var effectiveModifiers = Modifiers;

        if (TryGetMappedMetadata(nameof(Key), inputPacket, out var metaKey)
            && Enum.TryParse<Key>(metaKey, ignoreCase: true, out var parsedKey))
            effectiveKey = parsedKey;

        if (TryGetMappedMetadata(nameof(Modifiers), inputPacket, out var metaMod)
            && Enum.TryParse<ModifierKeys>(metaMod, ignoreCase: true, out var parsedMod))
            effectiveModifiers = parsedMod;

        if (effectiveModifiers == ModifierKeys.None)
            await actionService.PressKeyAsync(effectiveKey, ct).ConfigureAwait(false);
        else
            await actionService.PressKeyWithModifiersAsync(effectiveKey, effectiveModifiers, ct).ConfigureAwait(false);

        LogToBlackBox($"[КЛАВИША] Нажата: {(effectiveModifiers != ModifierKeys.None ? effectiveModifiers + "+" : "")}{effectiveKey}");
        return NodeResult.Success(null);
    }
}
