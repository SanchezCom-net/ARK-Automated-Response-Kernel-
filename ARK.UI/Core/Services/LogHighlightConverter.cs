using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using WpfCursors = System.Windows.Input.Cursors;
using System.Windows.Navigation;
using WpfColor = System.Windows.Media.Color;

namespace ARK.UI.Core.Services;

/// <summary>
/// Подсветка совпадений в строке лога. Вход: [0] — строка лога, [1] — поисковый запрос.
/// Поиск регистронезависимый, по частичному совпадению (подстрока: «инициал» найдёт
/// «инициализация»). Если оригинальный запрос не дал совпадений в строке — автоматически
/// пробуется запрос, транслитерированный из неверной раскладки («кдщп» ↔ «log», «ghbdtn» ↔ «привет»).
/// Совпадения «сияют» золотом (#FFF5D77F, Bold) на фоне приглушённых участков (#505050).
/// </summary>
public sealed class LogHighlightConverter : IMultiValueConverter
{
    private const int MinQueryLength = 2;

    // Frozen-кисти — общие для всех строк, ноль аллокаций на повторные вызовы
    private static readonly SolidColorBrush DefaultBrush = CreateFrozen(0xA0, 0xA0, 0xA0);
    private static readonly SolidColorBrush DimBrush     = CreateFrozen(0x50, 0x50, 0x50);
    private static readonly SolidColorBrush GoldBrush    = CreateFrozen(0xF5, 0xD7, 0x7F);
    private static readonly SolidColorBrush LinkBrush    = CreateFrozen(0x33, 0x99, 0xFF);

    internal static readonly Regex UrlRegex =
        new(@"https?://[^\s""'}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var text  = values is [string l, ..] ? l : string.Empty;
        var raw   = values is [_, string q, ..] ? q : string.Empty;
        var query = GetQuery(raw);

        // Монолитный режим: весь LogOutputText разбивается по \n.
        // Один вызов конвертера на всё обновление лога вместо N вызовов на N строк.
        var lines   = text.Split('\n');
        var inlines = new List<Inline>(lines.Length * 4);
        for (var i = 0; i < lines.Length; i++)
        {
            inlines.AddRange(BuildInlines(lines[i], query));
            if (i < lines.Length - 1)
                inlines.Add(new LineBreak());
        }
        return inlines;
    }

    // Строит список Inline для одной строки лога: URL → Hyperlink, совпадения → золото.
    // Вызывается как из IMultiValueConverter (ListBox), так и из LogsDocumentBehavior (монолит).
    internal static List<Inline> BuildInlines(string line, CachedQuery query)
    {
        var inlines = new List<Inline>(4);

        var urlMatches = UrlRegex.Matches(line);
        if (urlMatches.Count == 0)
        {
            AddHighlightedSegment(inlines, line, query);
            return inlines;
        }

        // Сегментируем строку: текст → гиперссылка → текст → …
        var pos = 0;
        foreach (Match urlMatch in urlMatches)
        {
            if (urlMatch.Index > pos)
                AddHighlightedSegment(inlines, line[pos..urlMatch.Index], query);

            if (Uri.TryCreate(urlMatch.Value, UriKind.Absolute, out var uri))
            {
                var hl = new Hyperlink(new Run(urlMatch.Value))
                {
                    Foreground  = LinkBrush,
                    NavigateUri = uri,
                    Cursor      = WpfCursors.Hand,
                    ForceCursor = true,
                };
                hl.RequestNavigate += Hyperlink_RequestNavigate;
                inlines.Add(hl);
            }
            else
            {
                AddHighlightedSegment(inlines, urlMatch.Value, query);
            }

            pos = urlMatch.Index + urlMatch.Length;
        }

        if (pos < line.Length)
            AddHighlightedSegment(inlines, line[pos..], query);

        return inlines;
    }

    // Потокобезопасная версия BuildInlines: возвращает DTO без WPF-элементов.
    // Может вызываться из Task.Run (фонового потока); WPF-элементы не создаются.
    internal static ParsedLogLine ParseLineToSegments(string line, CachedQuery query)
    {
        var segments = new List<(string, bool, string?)>(4);
        var hasQuery = query.Primary is not null;

        var urlMatches = UrlRegex.Matches(line);
        if (urlMatches.Count == 0)
        {
            CollectSegments(segments, line, query);
            return new ParsedLogLine { Segments = segments, HasActiveQuery = hasQuery };
        }

        var pos = 0;
        foreach (Match urlMatch in urlMatches)
        {
            if (urlMatch.Index > pos)
                CollectSegments(segments, line[pos..urlMatch.Index], query);

            // URL-сегмент: Url != null сигнализирует LogsDocumentBehavior создать Hyperlink
            segments.Add((urlMatch.Value, false, urlMatch.Value));
            pos = urlMatch.Index + urlMatch.Length;
        }

        if (pos < line.Length)
            CollectSegments(segments, line[pos..], query);

        return new ParsedLogLine { Segments = segments, HasActiveQuery = hasQuery };
    }

    // Добавляет в список сегменты с поисковой подсветкой (без WPF-элементов)
    private static void CollectSegments(
        List<(string Text, bool IsHighlight, string? Url)> segments,
        string text,
        CachedQuery query)
    {
        if (text.Length == 0) return;

        if (query.Primary is null)
        {
            segments.Add((text, false, null));
            return;
        }

        var matches = query.Primary.Matches(text);
        if (matches.Count == 0 && query.Secondary is not null)
            matches = query.Secondary.Matches(text);

        if (matches.Count == 0)
        {
            segments.Add((text, false, null)); // DimBrush — нет совпадений, строка приглушается
            return;
        }

        var pos = 0;
        foreach (Match m in matches)
        {
            if (m.Index > pos)
                segments.Add((text[pos..m.Index], false, null));
            segments.Add((m.Value, true, null)); // IsHighlight=true → GoldBrush+Bold
            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
            segments.Add((text[pos..], false, null));
    }

    // Наносит поисковую подсветку на произвольный текстовый сегмент (не-URL часть строки)
    private static void AddHighlightedSegment(List<Inline> inlines, string text, CachedQuery query)
    {
        if (text.Length == 0) return;

        if (query.Primary is null)
        {
            inlines.Add(new Run(text) { Foreground = DefaultBrush });
            return;
        }

        // Сначала оригинальный запрос; при нуле совпадений — fallback на исправленную раскладку
        var matches = query.Primary.Matches(text);
        if (matches.Count == 0 && query.Secondary is not null)
            matches = query.Secondary.Matches(text);

        if (matches.Count == 0)
        {
            // Совпадений нет — сегмент приглушается, контраст подчёркивает найденные строки
            inlines.Add(new Run(text) { Foreground = DimBrush });
            return;
        }

        var pos = 0;
        foreach (Match m in matches)
        {
            if (m.Index > pos)
                inlines.Add(new Run(text[pos..m.Index]) { Foreground = DimBrush });

            inlines.Add(new Run(m.Value)
            {
                Foreground = GoldBrush,
                FontWeight = System.Windows.FontWeights.Bold
            });
            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
            inlines.Add(new Run(text[pos..]) { Foreground = DimBrush });
    }

    private static void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
        e.Handled = true;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    // ── Кэш запроса (общий для подсветки и DataTrigger левой границы) ────────────

    internal sealed record CachedQuery(string Normalized, Regex? Primary, string Converted, Regex? Secondary);

    private static readonly Regex MultiSpaceRegex =
        new(@"\s{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly CachedQuery EmptyQuery = new(string.Empty, null, string.Empty, null);

    // Конвертеры вызываются ~150 раз (на каждую строку) при каждом изменении запроса:
    // Regex оригинала и раскладочного fallback'а строятся один раз на запрос.
    // WPF-конвертеры работают только в UI-потоке — статический кэш безопасен.
    private static string?     _rawKey;
    private static CachedQuery _cached = EmptyQuery;

    internal static CachedQuery GetQuery(string raw)
    {
        if (_rawKey == raw) return _cached;
        _rawKey = raw;

        // Нормализация: обрезка краёв + схлопывание двойных пробелов
        var normalized = MultiSpaceRegex.Replace(raw.Trim(), " ");
        if (normalized.Length < MinQueryLength)
            return _cached = EmptyQuery;

        var primary   = BuildRegex(normalized);
        var converted = TryConvertLayout(normalized);

        var secondary = converted.Length >= MinQueryLength
                     && !string.Equals(converted, normalized, StringComparison.OrdinalIgnoreCase)
            ? BuildRegex(converted)
            : null;

        return _cached = new CachedQuery(
            normalized, primary, secondary is null ? string.Empty : converted, secondary);
    }

    private static Regex BuildRegex(string query)
        => new(Regex.Escape(query), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ── Транслитерация неверной раскладки (QWERTY ↔ ЙЦУКЕН) ─────────────────────

    // Позиционное соответствие клавиш: en[i] и ru[i] — одна физическая клавиша
    private const string EnChars =
        "qwertyuiop[]asdfghjkl;'zxcvbnm,./`" +
        "QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?~";
    private const string RuChars =
        "йцукенгшщзхъфывапролджэячсмитьбю.ё" +
        "ЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,Ё";

    private static readonly FrozenDictionary<char, char> EnToRu = BuildMap(EnChars, RuChars);
    private static readonly FrozenDictionary<char, char> RuToEn = BuildMap(RuChars, EnChars);

    /// <summary>
    /// Конвертирует запрос, случайно набранный в неверной раскладке:
    /// «ghbdtn» → «привет», «кдщп» → «log». Направление определяется преобладающим
    /// алфавитом; символы вне карты остаются как есть.
    /// </summary>
    private static string TryConvertLayout(string input)
    {
        int latin = 0, cyrillic = 0;
        foreach (var ch in input)
        {
            if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))            latin++;
            else if (ch is (>= 'а' and <= 'я') or (>= 'А' and <= 'Я') or 'ё' or 'Ё') cyrillic++;
        }
        if (latin == 0 && cyrillic == 0) return input;

        var map = latin >= cyrillic ? EnToRu : RuToEn;
        return string.Create(input.Length, (input, map), static (span, state) =>
        {
            var (src, m) = state;
            for (var i = 0; i < span.Length; i++)
                span[i] = m.TryGetValue(src[i], out var mapped) ? mapped : src[i];
        });
    }

    private static FrozenDictionary<char, char> BuildMap(string from, string to)
    {
        var dict = new Dictionary<char, char>(from.Length);
        for (var i = 0; i < from.Length; i++)
            dict[from[i]] = to[i];
        return dict.ToFrozenDictionary();
    }

    private static SolidColorBrush CreateFrozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(WpfColor.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// True, если строка лога содержит запрос (без учёта регистра) — в оригинальной
/// или транслитерированной раскладке. Питает DataTrigger золотой левой границы строки.
/// </summary>
public sealed class LogMatchConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var line = values is [string l, ..] ? l : string.Empty;
        var raw  = values is [_, string q, ..] ? q : string.Empty;

        var query = LogHighlightConverter.GetQuery(raw);
        if (query.Primary is null) return false;

        return line.Contains(query.Normalized, StringComparison.OrdinalIgnoreCase)
            || (query.Secondary is not null
                && line.Contains(query.Converted, StringComparison.OrdinalIgnoreCase));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
