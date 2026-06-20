using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ARK.UI.Core.Services;

/// <summary>
/// Attached behavior: кликабельность Hyperlink внутри TextBlock с IsHitTestVisible=False.
///
/// Архитектура двухслойного терминала:
///   Grid
///     ├── TextBox  (IsHitTestVisible=True)  — выделение / Ctrl+C
///     └── TextBlock (IsHitTestVisible=False) — золотая подсветка + URL Hyperlink
///
/// Проблема: TextBlock с IsHitTestVisible=False не получает события мыши вообще —
/// Hyperlink.RequestNavigate никогда не вызывается.
///
/// Решение: прикрепляем behavior к Grid. PreviewMouseLeftButtonDown (tunneling) проходит
/// через Grid до TextBox. Мы временно включаем IsHitTestVisible у TextBlock, вызываем
/// InputHitTest и поднимаемся по LogicalTree в поиске Hyperlink.
/// Найдено → открываем браузер + e.Handled=true (выделение не начинается).
/// Не найдено → событие идёт дальше в TextBox, выделение работает штатно.
/// </summary>
public static class HyperlinkClickBehavior
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(HyperlinkClickBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d)  => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        if ((bool)e.NewValue)
            el.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnPreviewMouseDown));
        else
            el.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnPreviewMouseDown));
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement container) return;

        var textBlock = FindDirectTextBlock(container);
        if (textBlock is null) return;

        // Все операции синхронны на UI-потоке: временное включение → HitTest → выключение.
        // IsHitTestVisible не вызывает layout invalidation, только invalidates input subsystem.
        textBlock.IsHitTestVisible = true;
        var hit = textBlock.InputHitTest(e.GetPosition(textBlock)) as DependencyObject;
        textBlock.IsHitTestVisible = false;

        // Run → Hyperlink → TextBlock: поднимаемся по логическому дереву
        var node = hit;
        while (node is not null)
        {
            if (node is Hyperlink { NavigateUri: { } uri })
            {
                Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
                e.Handled = true;   // выделение текста не начинается при клике по ссылке
                return;
            }
            node = LogicalTreeHelper.GetParent(node);
        }
    }

    // Ищет TextBlock среди прямых визуальных детей (Grid → TextBox, TextBlock).
    private static TextBlock? FindDirectTextBlock(FrameworkElement parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(parent, i) is TextBlock tb) return tb;
        }
        return null;
    }
}
