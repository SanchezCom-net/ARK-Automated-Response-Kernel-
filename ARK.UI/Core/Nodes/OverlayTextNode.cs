using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class OverlayTextNode : BaseNode
{
    public override string DefaultDataInputPropertyName => nameof(Text);

    private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; OnPropertyChanged(); } }
    }

    private int _durationMs = 2000;
    public int DurationMilliseconds
    {
        get => _durationMs;
        set { if (_durationMs != value) { _durationMs = value; OnPropertyChanged(); } }
    }

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

        // Smart Fields V3.6: метаданные шины переопределяют значение из UI
        if (TryGetMappedMetadata(nameof(Text), inputPacket, out var metaText)
            && !string.IsNullOrEmpty(metaText))
            Text = metaText;

        if (TryGetMappedMetadata(nameof(DurationMilliseconds), inputPacket, out var metaDur)
            && int.TryParse(metaDur, out int parsedDur))
            DurationMilliseconds = parsedDur;

        var overlayService = NodeServices!.GetRequiredService<IOverlayService>();
        await overlayService.ShowTextAsync(Text, DurationMilliseconds, ct).ConfigureAwait(false);
        LogToBlackBox($"[ОВЕРЛЕЙ] Текст выведен: '{Text}', длительность: {DurationMilliseconds} мс");
        return NodeResult.Success(null);
    }
}
