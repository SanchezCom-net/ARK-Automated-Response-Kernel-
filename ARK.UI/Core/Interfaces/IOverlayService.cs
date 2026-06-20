namespace ARK.UI.Core.Interfaces;

public interface IOverlayService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task ShowOverlayAsync(CancellationToken cancellationToken = default);
    Task HideOverlayAsync(CancellationToken cancellationToken = default);
    Task ShowTextAsync(string text, int durationMilliseconds,
        CancellationToken cancellationToken = default);
    Task ShowHighlightAsync(System.Windows.Point center, double width, double height,
        int durationMilliseconds, CancellationToken cancellationToken = default);
    Task ShowStreamingTextAsync(IAsyncEnumerable<string> textStream, CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
}
