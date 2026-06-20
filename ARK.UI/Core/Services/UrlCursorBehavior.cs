using System.Runtime.CompilerServices;
using System.Windows;
using WpfCursors    = System.Windows.Input.Cursors;
using WpfMouseArgs  = System.Windows.Input.MouseEventArgs;
using WpfTextBox    = System.Windows.Controls.TextBox;

namespace ARK.UI.Core.Services;

/// <summary>
/// Attached behavior: меняет курсор TextBox на Hand при наведении на URL-подстроку.
///
/// Проблема: TextBox (нижний слой) не знает, где в TextBlock (верхний слой, IsHitTestVisible=False)
/// находятся Hyperlink'и. При наведении на синюю ссылку курсор остаётся IBeam.
///
/// Решение: на каждый MouseMove получаем символьный индекс под курсором через
/// GetCharacterIndexFromPoint, проверяем его принадлежность к URL-диапазону и переключаем
/// курсор. UrlRegex.Matches кэшируется по тексту — пересчёт только при изменении LogOutputText.
/// ConditionalWeakTable не держит TextBox живым (weak key).
/// </summary>
public static class UrlCursorBehavior
{
    private static readonly ConditionalWeakTable<WpfTextBox, UrlCache> _cache = new();

    private sealed class UrlCache
    {
        public string?            Text;
        public (int Start, int End)[]? Ranges;
    }

    // ── Attached property ───────────────────────────────────────────────────

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(UrlCursorBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d)  => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WpfTextBox tb) return;
        if ((bool)e.NewValue)
            tb.MouseMove += OnMouseMove;
        else
            tb.MouseMove -= OnMouseMove;
    }

    // ── Обработчик MouseMove ────────────────────────────────────────────────

    private static void OnMouseMove(object sender, WpfMouseArgs e)
    {
        if (sender is not WpfTextBox tb) return;

        // snapToText=false: возвращает -1 если курсор не над символом (отступы, пустое место)
        var idx = tb.GetCharacterIndexFromPoint(e.GetPosition(tb), snapToText: false);

        var cursor = idx >= 0 && IsInUrlRange(tb, idx)
            ? WpfCursors.Hand
            : WpfCursors.IBeam;

        // Cursors.Hand / IBeam — синглтоны; ReferenceEquals избегает лишних invalidation
        if (!ReferenceEquals(tb.Cursor, cursor))
            tb.Cursor = cursor;
    }

    // ── Кэш URL-диапазонов ──────────────────────────────────────────────────

    private static bool IsInUrlRange(WpfTextBox tb, int idx)
    {
        var text  = tb.Text;
        var entry = _cache.GetOrCreateValue(tb);

        // Пересчитываем только при изменении текста (обновление лога)
        if (!string.Equals(entry.Text, text, StringComparison.Ordinal))
        {
            entry.Text = text;
            var matches = LogHighlightConverter.UrlRegex.Matches(text);
            if (matches.Count == 0)
            {
                entry.Ranges = null;
            }
            else
            {
                entry.Ranges = new (int, int)[matches.Count];
                for (var i = 0; i < matches.Count; i++)
                    entry.Ranges[i] = (matches[i].Index, matches[i].Index + matches[i].Length);
            }
        }

        if (entry.Ranges is null) return false;
        foreach (var (start, end) in entry.Ranges)
            if (idx >= start && idx < end) return true;
        return false;
    }
}
