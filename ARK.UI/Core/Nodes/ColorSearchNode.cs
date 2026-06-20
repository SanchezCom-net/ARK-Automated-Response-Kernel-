using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using WpfColor = System.Windows.Media.Color;

namespace ARK.UI.Core.Nodes;

public sealed class ColorSearchNode : BaseNode
{
    public WpfColor  TargetColor { get; set; }
    public SearchArea Area       { get; set; }
    /// <summary>Id ноды для перехода при успешном нахождении цвета.</summary>
    public Guid? FoundNodeId    { get; set; }

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        var visionService = serviceProvider.GetRequiredService<IVisionService>();
        var point = await visionService
            .FindColorAsync(TargetColor, Area, cancellationToken)
            .ConfigureAwait(false);

        if (point.HasValue)
        {
            OnSuccessNodeId = FoundNodeId;
            await logger.LogInfoAsync(Name,
                $"Цвет #{TargetColor.R:X2}{TargetColor.G:X2}{TargetColor.B:X2} найден: " +
                $"({point.Value.X:F0}, {point.Value.Y:F0})").ConfigureAwait(false);
            return true;
        }

        await logger.LogInfoAsync(Name, "Цвет не найден в указанной области.").ConfigureAwait(false);
        return false;
    }
}
