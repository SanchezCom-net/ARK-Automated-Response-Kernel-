using ARK.UI.Core.Interfaces;
using ARK.UI.Views;
using WpfApp   = System.Windows.Application;
using WpfPoint = System.Windows.Point;

namespace ARK.UI.Core.Services;

public sealed class OverlayService : IOverlayService
{
    private readonly ILogService _logger;
    private OverlayWindow? _window;

    public OverlayService(ILogService logger)
    {
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await WpfApp.Current.Dispatcher.InvokeAsync(() =>
            _window = new OverlayWindow());
        await _logger.LogInfoAsync(nameof(OverlayService), "Оверлей инициализирован.");
    }

    public async Task ShowOverlayAsync(CancellationToken cancellationToken = default)
    {
        await WpfApp.Current.Dispatcher.InvokeAsync(() =>
        {
            _window ??= new OverlayWindow();
            _window.Show();
        });
        await _logger.LogInfoAsync(nameof(OverlayService), "Оверлей показан.");
    }

    public async Task HideOverlayAsync(CancellationToken cancellationToken = default)
    {
        await WpfApp.Current.Dispatcher.InvokeAsync(() => _window?.Hide());
        await _logger.LogInfoAsync(nameof(OverlayService), "Оверлей скрыт.");
    }

    public async Task ShowTextAsync(string text, int durationMilliseconds,
        CancellationToken cancellationToken = default)
    {
        OverlayWindow? window = null;
        await WpfApp.Current.Dispatcher.InvokeAsync(() =>
        {
            _window ??= new OverlayWindow();
            if (!_window.IsVisible)
                _window.Show();
            window = _window;
        });

        await _logger.LogInfoAsync(nameof(OverlayService),
            $"Оверлей: вывод текста на {durationMilliseconds} мс.");

        if (window is not null)
            await window.SetTextAsync(text, durationMilliseconds, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task ShowHighlightAsync(
        WpfPoint center, double width, double height,
        int durationMilliseconds, CancellationToken cancellationToken = default)
    {
        OverlayWindow? window = null;
        await WpfApp.Current.Dispatcher.InvokeAsync(() =>
        {
            _window ??= new OverlayWindow();
            if (!_window.IsVisible)
                _window.Show();
            window = _window;
        });

        await _logger.LogInfoAsync(nameof(OverlayService),
            $"Подсветка шаблона: ({center.X:F0}, {center.Y:F0}), {width:F0}×{height:F0}");

        if (window is not null)
            await window.ShowHighlightAsync(center, width, height, durationMilliseconds, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task ShowStreamingTextAsync(
        IAsyncEnumerable<string> textStream,
        CancellationToken cancellationToken = default)
    {
        OverlayWindow? window = null;
        await WpfApp.Current.Dispatcher.InvokeAsync(() =>
        {
            _window ??= new OverlayWindow();
            if (!_window.IsVisible)
                _window.Show();
            window = _window;
        });

        await _logger.LogInfoAsync(nameof(OverlayService), "[ИИ] Потоковый вывод ответа в оверлей.");

        if (window is not null)
            await window.ShowStreamingTextAsync(textStream, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await WpfApp.Current.Dispatcher.InvokeAsync(() =>
        {
            _window?.Close();
            _window = new OverlayWindow();
            _window.Show();
        });
        await _logger.LogInfoAsync(nameof(OverlayService), "Оверлей пересоздан.");
    }
}
