using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Threading;

namespace ARK.UI.Core.Services;

/// <summary>
/// Attached-свойство Source для доставки коллекции Inline в TextBlock через Binding.
/// Необходимо, потому что TextBlock.Inlines — обычное CLR-свойство (не DependencyProperty)
/// и напрямую биндингу/MultiBinding не поддаётся.
/// </summary>
public static class LogInlinesBehavior
{
    // ── Source: IEnumerable<Inline> из MultiBinding → LogHighlightConverter ──────

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source",
            typeof(object),
            typeof(LogInlinesBehavior),
            new PropertyMetadata(null, OnSourceChanged));

    public static void    SetSource(DependencyObject element, object? value) => element.SetValue(SourceProperty, value);
    public static object? GetSource(DependencyObject element)               => element.GetValue(SourceProperty);

    // ── SyncSourceText: эталон для проверки синхронизации TextBlock ─────────────
    // Привязывается к LogOutputText (тот же источник, что у TextBox).
    // Self-Healing сверяет: \n в тексте == LineBreak в Inlines.
    // Рассинхрон возникает при исключении в LogHighlightConverter:
    // TextBlock очищается, TextBox продолжает показывать полный текст.

    public static readonly DependencyProperty SyncSourceTextProperty =
        DependencyProperty.RegisterAttached(
            "SyncSourceText",
            typeof(string),
            typeof(LogInlinesBehavior),
            new PropertyMetadata(null));

    public static void    SetSyncSourceText(DependencyObject element, string? value) => element.SetValue(SyncSourceTextProperty, value);
    public static string? GetSyncSourceText(DependencyObject element)               => (string?)element.GetValue(SyncSourceTextProperty);

    // Флаг: предотвращает рекурсивный цикл при повторном сбое конвертера
    private static readonly DependencyProperty HealingProperty =
        DependencyProperty.RegisterAttached(
            "Healing",
            typeof(bool),
            typeof(LogInlinesBehavior),
            new PropertyMetadata(false));

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock) return;

        textBlock.Inlines.Clear();
        if (e.NewValue is IEnumerable<Inline> inlines)
            textBlock.Inlines.AddRange(inlines);

        // Self-Healing: O(n) счётчик, никакого сравнения строк.
        // Если LogHighlightConverter выбросил исключение, e.NewValue не будет
        // IEnumerable<Inline> — Inlines очистится, но TextBox останется полным.
        // При расхождении счётчиков принудительно перезапускаем MultiBinding на
        // фоновом приоритете (после текущего layout-прохода), один раз (guard флаг).
        var sourceText = GetSyncSourceText(textBlock);
        if (string.IsNullOrEmpty(sourceText) || (bool)textBlock.GetValue(HealingProperty))
            return;

        var expectedLineBreaks = sourceText.Count(c => c == '\n');
        var actualLineBreaks   = textBlock.Inlines.OfType<LineBreak>().Count();

        if (actualLineBreaks == expectedLineBreaks) return;

        textBlock.SetValue(HealingProperty, true);
        _ = textBlock.Dispatcher.InvokeAsync(() =>
        {
            BindingOperations.GetMultiBindingExpression(textBlock, SourceProperty)?.UpdateTarget();
            textBlock.SetValue(HealingProperty, false);
        }, DispatcherPriority.Background);
    }
}
