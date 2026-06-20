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

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        var visionService  = serviceProvider.GetRequiredService<IVisionService>();
        var overlayService = serviceProvider.GetRequiredService<IOverlayService>();

        var point = await visionService
            .FindTemplateAsync(TemplateBgra, TemplateWidth, TemplateHeight,
                               Area, Tolerance, cancellationToken)
            .ConfigureAwait(false);

        if (point.HasValue)
        {
            await logger.LogInfoAsync(Name,
                $"Шаблон найден: ({point.Value.X:F0}, {point.Value.Y:F0})").ConfigureAwait(false);

            // Fire-and-forget: золотая рамка на оверлее вокруг найденного шаблона
            _ = overlayService.ShowHighlightAsync(
                point.Value, TemplateWidth, TemplateHeight,
                durationMilliseconds: 2000,
                cancellationToken: cancellationToken);

            return true;
        }

        await logger.LogInfoAsync(Name, "Шаблон не найден в указанной области.").ConfigureAwait(false);
        return false;
    }
}
