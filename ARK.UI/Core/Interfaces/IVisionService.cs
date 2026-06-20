using ARK.UI.Core.Models;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace ARK.UI.Core.Interfaces;

public interface IVisionService
{
    // Захват основного экрана → JPEG ~75% (Base64-ready для multimodal запросов)
    Task<byte[]> CapturePrimaryScreenAsync(CancellationToken cancellationToken = default);

    Task<WpfPoint?> FindColorAsync(
        WpfColor targetColor,
        SearchArea area,
        CancellationToken cancellationToken = default);

    Task<WpfPoint?> FindTemplateAsync(
        byte[] templateBgra,
        int templateWidth,
        int templateHeight,
        SearchArea area,
        double tolerance = 0.1,
        CancellationToken cancellationToken = default);
}
