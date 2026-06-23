using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using WpfColor = System.Windows.Media.Color;

namespace ARK.UI.Core.Nodes;

public sealed class ColorSearchNode : BaseNode
{
    public WpfColor  TargetColor { get; set; }
    public SearchArea Area       { get; set; }
    public Guid? FoundNodeId    { get; set; }

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        var visionService = NodeServices!.GetRequiredService<IVisionService>();
        var point = await visionService
            .FindColorAsync(TargetColor, Area, ct)
            .ConfigureAwait(false);

        if (point.HasValue)
        {
            OnSuccessNodeId = FoundNodeId;
            await NodeLogger!.LogInfoAsync(Name,
                $"Цвет #{TargetColor.R:X2}{TargetColor.G:X2}{TargetColor.B:X2} найден: " +
                $"({point.Value.X:F0}, {point.Value.Y:F0})").ConfigureAwait(false);
            return NodeResult.Success(null);
        }

        await NodeLogger!.LogInfoAsync(Name, "Цвет не найден в указанной области.").ConfigureAwait(false);
        return NodeResult.Failure("Цвет не найден.");
    }
}
