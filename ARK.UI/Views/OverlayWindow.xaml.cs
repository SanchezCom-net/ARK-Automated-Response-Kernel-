using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using WpfBrush = System.Windows.Media.Brush;
using System.Windows.Media.Imaging;
using ARK.UI.Core.Services;
using WpfPoint = System.Windows.Point;

namespace ARK.UI.Views;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/app.ico", UriKind.RelativeOrAbsolute)); }
        catch { }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetFullScreen();
        ApplyClickThrough();
    }

    public async Task SetTextAsync(string text, int durationMilliseconds,
        CancellationToken cancellationToken)
    {
        await Dispatcher.InvokeAsync(() => SetColoredInlines(text));
        await FadeAsync(0d, 1d, 300);
        await Task.Delay(durationMilliseconds, cancellationToken);
        await FadeAsync(1d, 0d, 300);
        await Dispatcher.InvokeAsync(() => OverlayTextBlock.Inlines.Clear());
    }

    // Разбивает текст на Run-элементы: слово ACTIVE — рубиновым, остальное — золотым.
    private void SetColoredInlines(string text)
    {
        OverlayTextBlock.Inlines.Clear();
        const string keyword = "ACTIVE";
        int idx = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            OverlayTextBlock.Inlines.Add(new Run(text));
            return;
        }
        var gold = TryFindResource("GoldBrush") as WpfBrush;
        var ruby = TryFindResource("RubyBrush") as WpfBrush;
        if (idx > 0)
            OverlayTextBlock.Inlines.Add(new Run(text[..idx]) { Foreground = gold });
        OverlayTextBlock.Inlines.Add(new Run(text[idx..(idx + keyword.Length)]) { Foreground = ruby });
        if (idx + keyword.Length < text.Length)
            OverlayTextBlock.Inlines.Add(new Run(text[(idx + keyword.Length)..]) { Foreground = gold });
    }

    public async Task ShowHighlightAsync(
        WpfPoint center, double width, double height,
        int durationMs, CancellationToken cancellationToken)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            HighlightRect.Width  = width;
            HighlightRect.Height = height;
            HighlightRect.Margin = new Thickness(center.X - width / 2, center.Y - height / 2, 0, 0);
        });
        await FadeElementAsync(HighlightRect, 0d, 1d, 300);
        await Task.Delay(durationMs, cancellationToken);
        await FadeElementAsync(HighlightRect, 1d, 0d, 300);
    }

    private Task FadeAsync(double from, double to, int durationMs)
        => FadeElementAsync(OverlayBorder, from, to, durationMs);

    private Task FadeElementAsync(UIElement element, double from, double to, int durationMs)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.InvokeAsync(() =>
        {
            var animation = new DoubleAnimation(from, to,
                new Duration(TimeSpan.FromMilliseconds(durationMs)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            animation.Completed += (_, _) => tcs.TrySetResult();
            element.BeginAnimation(OpacityProperty, animation);
        });
        return tcs.Task;
    }

    public async Task ShowStreamingTextAsync(IAsyncEnumerable<string> textStream, CancellationToken ct)
    {
        await Dispatcher.InvokeAsync(() => AiResponseTextBlock.Text = string.Empty);
        var visible = false;
        try
        {
            await foreach (var token in textStream.WithCancellation(ct).ConfigureAwait(false))
            {
                if (!visible)
                {
                    visible = true;
                    await FadeElementAsync(AiResponseBorder, 0d, 1d, 300);
                }
                await Dispatcher.InvokeAsync(() => AiResponseTextBlock.Text += token);
            }
            if (visible)
                await Task.Delay(3500, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (visible)
            {
                await FadeElementAsync(AiResponseBorder, 1d, 0d, 400);
                await Dispatcher.InvokeAsync(() => AiResponseTextBlock.Text = string.Empty);
            }
        }
    }

    private void SetFullScreen()
    {
        var area = SystemParameters.WorkArea;
        Left   = area.Left;
        Top    = area.Top;
        Width  = area.Width;
        Height = area.Height - 2;
    }

    private void ApplyClickThrough()
    {
        var hwnd    = new WindowInteropHelper(this).Handle;
        int exStyle = Win32Api.GetWindowLong(hwnd, Win32Api.GWL_EXSTYLE);
        exStyle |= Win32Api.WS_EX_TRANSPARENT | Win32Api.WS_EX_TOOLWINDOW | Win32Api.WS_EX_NOACTIVATE;
        Win32Api.SetWindowLong(hwnd, Win32Api.GWL_EXSTYLE, exStyle);
    }
}
