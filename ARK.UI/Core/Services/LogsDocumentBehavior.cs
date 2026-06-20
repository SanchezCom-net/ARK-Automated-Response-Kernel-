using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using WpfColor = System.Windows.Media.Color;
using WpfRtb   = System.Windows.Controls.RichTextBox;

namespace ARK.UI.Core.Services;

/// <summary>
/// Промежуточное представление строки лога: только сырые сегменты без WPF-элементов.
/// Создаётся в фоновом потоке (Task.Run), WPF-элементы строятся только на UI-потоке.
/// </summary>
public sealed class ParsedLogLine
{
    public List<(string Text, bool IsHighlight, string? Url)> Segments { get; init; } = [];
    public bool HasActiveQuery { get; init; }
}

/// <summary>
/// Attached behavior для монолитного терминала логов.
/// Rebuild разбивает работу: парсинг строк — Task.Run (фон),
/// создание WPF-элементов — UI-поток. Фризы UI исключены.
/// </summary>
public static class LogsDocumentBehavior
{
    // ── Frozen-кисти (автономны от LogHighlightConverter) ────────────────────

    private static readonly SolidColorBrush DefaultBrush = Frozen(0xA0, 0xA0, 0xA0);
    private static readonly SolidColorBrush DimBrush     = Frozen(0x50, 0x50, 0x50);
    private static readonly SolidColorBrush GoldBrush    = Frozen(0xF5, 0xD7, 0x7F);
    private static readonly SolidColorBrush LinkBrush    = Frozen(0x33, 0x99, 0xFF);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var b2 = new SolidColorBrush(WpfColor.FromRgb(r, g, b));
        b2.Freeze();
        return b2;
    }

    // ── Lines ────────────────────────────────────────────────────────────────

    public static readonly DependencyProperty LinesProperty =
        DependencyProperty.RegisterAttached(
            "Lines", typeof(IEnumerable<string>), typeof(LogsDocumentBehavior),
            new PropertyMetadata(null, OnLinesChanged));

    public static IEnumerable<string>? GetLines(DependencyObject d)
        => (IEnumerable<string>?)d.GetValue(LinesProperty);
    public static void SetLines(DependencyObject d, IEnumerable<string>? v)
        => d.SetValue(LinesProperty, v);

    // ── Query ────────────────────────────────────────────────────────────────

    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.RegisterAttached(
            "Query", typeof(string), typeof(LogsDocumentBehavior),
            new PropertyMetadata(string.Empty, OnQueryChanged));

    public static string? GetQuery(DependencyObject d)
        => (string?)d.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject d, string? v)
        => d.SetValue(QueryProperty, v);

    // ── Private: хранит обработчик коллекции per-element ────────────────────

    private static readonly DependencyProperty CollectionHandlerProperty =
        DependencyProperty.RegisterAttached(
            "CollectionHandler",
            typeof(NotifyCollectionChangedEventHandler),
            typeof(LogsDocumentBehavior),
            new PropertyMetadata(null));

    // ── Callbacks ────────────────────────────────────────────────────────────

    private static void OnLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WpfRtb rtb) return;

        if (e.OldValue is INotifyCollectionChanged oldCol)
        {
            var old = (NotifyCollectionChangedEventHandler?)rtb.GetValue(CollectionHandlerProperty);
            if (old is not null) oldCol.CollectionChanged -= old;
        }

        if (e.NewValue is INotifyCollectionChanged newCol)
        {
            NotifyCollectionChangedEventHandler h = (_, _) => Rebuild(rtb, scrollToEnd: true);
            rtb.SetValue(CollectionHandlerProperty, h);
            newCol.CollectionChanged += h;
        }

        Rebuild(rtb, scrollToEnd: true);
    }

    private static void OnQueryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WpfRtb rtb) return;
        Rebuild(rtb, scrollToEnd: false);
    }

    // ── Async document builder ────────────────────────────────────────────────

    private static async void Rebuild(WpfRtb rtb, bool scrollToEnd)
    {
        // Снапшот до ухода в фон: ObservableCollection не thread-safe для итерации
        var lines = (GetLines(rtb) ?? []).ToList();
        var query = LogHighlightConverter.GetQuery(GetQuery(rtb) ?? string.Empty);

        // ── Фоновый поток: парсинг строк → List<ParsedLogLine> без WPF-объектов ─
        var parsedLines = await Task.Run(() =>
        {
            var result = new List<ParsedLogLine>(lines.Count);
            foreach (var line in lines)
                result.Add(LogHighlightConverter.ParseLineToSegments(line, query));
            return result;
        }).ConfigureAwait(true); // ConfigureAwait(true) = возврат на UI-поток

        // ── UI-поток: строим ОДИН Paragraph в памяти (он ещё не в документе!) ─
        //
        // Ключевой инсайт: Inlines.Add на ОТСОЕДИНЁННОМ Paragraph не вызывает
        // layout-инвалидацию. Когда параграф присоединён к живому документу,
        // каждый Add ставит в очередь Dispatcher отдельный Measure/Arrange-проход.
        // Итог: document.Blocks.Add вызывается 1 раз вместо 150 →
        // 1 layout-проход вместо 150+, фриз UI устранён.
        var para = new Paragraph { Margin = new Thickness(0), Padding = new Thickness(0) };

        for (var i = 0; i < parsedLines.Count; i++)
        {
            var parsedLine = parsedLines[i];
            foreach (var (text, isHighlight, url) in parsedLine.Segments)
            {
                if (url is not null)
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        var hl = new Hyperlink(new Run(text)) { Foreground = LinkBrush, NavigateUri = uri };
                        hl.RequestNavigate += HyperlinkNavigate;
                        para.Inlines.Add(hl);
                    }
                    else
                    {
                        para.Inlines.Add(new Run(text) { Foreground = DimBrush });
                    }
                }
                else
                {
                    para.Inlines.Add(new Run(text)
                    {
                        Foreground = isHighlight             ? GoldBrush
                                    : parsedLine.HasActiveQuery ? DimBrush
                                    : DefaultBrush,
                        FontWeight = isHighlight
                                    ? System.Windows.FontWeights.Bold
                                    : System.Windows.FontWeights.Normal,
                    });
                }
            }
            // LineBreak легче Paragraph: просто маркер новой строки в одном блоке
            if (i < parsedLines.Count - 1)
                para.Inlines.Add(new LineBreak());
        }

        // ── Единственный layout-проход: Clear + Add(para) ──────────────────────
        var document = rtb.Document;
        document.PagePadding = new Thickness(4, 2, 4, 2);
        document.PageWidth   = 8192;
        document.FontFamily  = rtb.FontFamily;
        document.FontSize    = rtb.FontSize;

        rtb.BeginChange();
        try
        {
            document.Blocks.Clear();
            document.Blocks.Add(para);   // 1 вызов = 1 layout-инвалидация
        }
        finally
        {
            rtb.EndChange();
        }

        if (scrollToEnd)
            rtb.ScrollToEnd();
    }

    private static void HyperlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
        e.Handled = true;
    }
}
