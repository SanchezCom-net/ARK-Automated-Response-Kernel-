using System.Windows;
using System.Windows.Controls;

namespace ARK.UI.Core.Services;

/// <summary>
/// Attached behavior для SharedLogsScrollViewer без Code-Behind.
/// ScrollToEndTrigger: привязывается к LogOutputText; при изменении прокручивает
/// ScrollViewer в конец после завершения layout-прохода (Background priority).
/// </summary>
public static class ScrollSyncBehavior
{
    public static readonly DependencyProperty ScrollToEndTriggerProperty =
        DependencyProperty.RegisterAttached(
            "ScrollToEndTrigger", typeof(string), typeof(ScrollSyncBehavior),
            new PropertyMetadata(null, OnScrollToEndTriggerChanged));

    public static string? GetScrollToEndTrigger(DependencyObject d)
        => (string?)d.GetValue(ScrollToEndTriggerProperty);
    public static void SetScrollToEndTrigger(DependencyObject d, string? v)
        => d.SetValue(ScrollToEndTriggerProperty, v);

    private static void OnScrollToEndTriggerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv || e.NewValue is null) return;
        // Background-приоритет: ScrollableHeight пересчитан после Measure/Arrange
        sv.Dispatcher.InvokeAsync(
            () => sv.ScrollToVerticalOffset(sv.ScrollableHeight),
            System.Windows.Threading.DispatcherPriority.Background);
    }
}
