using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class TemplateMatchNode : BaseNode
{
    public byte[]    TemplateBgra   { get; set; } = [];
    public int       TemplateWidth  { get; set; }
    public int       TemplateHeight { get; set; }
    public SearchArea Area          { get; set; }
    public double    Tolerance      { get; set; } = 0.1;

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        var visionService  = NodeServices!.GetRequiredService<IVisionService>();
        var overlayService = NodeServices!.GetRequiredService<IOverlayService>();

        var point = await visionService
            .FindTemplateAsync(TemplateBgra, TemplateWidth, TemplateHeight, Area, Tolerance, ct)
            .ConfigureAwait(false);

        if (point.HasValue)
        {
            await NodeLogger!.LogInfoAsync(Name,
                $"Шаблон найден: ({point.Value.X:F0}, {point.Value.Y:F0})").ConfigureAwait(false);

            _ = overlayService.ShowHighlightAsync(point.Value, TemplateWidth, TemplateHeight,
                durationMilliseconds: 2000, cancellationToken: ct);

            return NodeResult.Success(null);
        }

        await NodeLogger!.LogInfoAsync(Name, "Шаблон не найден в указанной области.").ConfigureAwait(false);
        return NodeResult.Failure("Шаблон не найден.");
    }
}
