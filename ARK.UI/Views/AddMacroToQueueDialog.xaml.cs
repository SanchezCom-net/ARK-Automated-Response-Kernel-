using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ARK.UI.Core.Models;
using WpfKeyEventArgs         = System.Windows.Input.KeyEventArgs;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace ARK.UI.Views;

public partial class AddMacroToQueueDialog : Window
{
    // ── Внутреннее представление строки списка ────────────────────────────

    public sealed record MacroDisplayItem(
        string     Name,
        string     DisplayPath,
        MacroEntry Macro,
        AppProfile Profile);

    // ── Публичный результат ───────────────────────────────────────────────

    public (MacroEntry Macro, AppProfile Profile, string Path)? SelectedMacro { get; private set; }

    // ── Приватное состояние ───────────────────────────────────────────────

    private readonly List<MacroDisplayItem> _allItems;

    // ── Конструктор ───────────────────────────────────────────────────────

    public AddMacroToQueueDialog(
        IEnumerable<(MacroEntry Macro, AppProfile Profile, string Path)> allMacros)
    {
        _allItems = allMacros
            .Select(t => new MacroDisplayItem(t.Macro.Name, t.Path, t.Macro, t.Profile))
            .OrderBy(i => i.Name)
            .ToList();

        InitializeComponent();
        ApplyFilter(string.Empty);
        Loaded += (_, _) => SearchBox.Focus();
    }

    // ── Фильтрация по подстроке (регистронезависимо) ──────────────────────

    private void ApplyFilter(string text)
    {
        MacroList.Items.Clear();
        var lower = text.Trim().ToLowerInvariant();

        foreach (var item in _allItems)
        {
            if (string.IsNullOrEmpty(lower)
                || item.Name.Contains(lower, StringComparison.OrdinalIgnoreCase)
                || item.DisplayPath.Contains(lower, StringComparison.OrdinalIgnoreCase))
            {
                MacroList.Items.Add(item);
            }
        }

        if (MacroList.Items.Count == 1)
            MacroList.SelectedIndex = 0;
    }

    // ── Обработчики событий ───────────────────────────────────────────────

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        => ApplyFilter(SearchBox.Text);

    private void OnTitleBarDrag(object sender, WpfMouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnAddClick(object sender, RoutedEventArgs e) => Confirm();

    private void OnListDoubleClick(object sender, WpfMouseButtonEventArgs e)
    {
        if (MacroList.SelectedItem is MacroDisplayItem)
            Confirm();
    }

    private void OnKeyDown(object sender, WpfKeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (MacroList.SelectedItem is MacroDisplayItem) Confirm();
                break;
            case Key.Escape:
                Close();
                break;
            case Key.Down:
                MacroList.Focus();
                if (MacroList.Items.Count > 0)
                    MacroList.SelectedIndex = Math.Max(0, MacroList.SelectedIndex);
                break;
        }
    }

    private void Confirm()
    {
        if (MacroList.SelectedItem is not MacroDisplayItem item) return;
        SelectedMacro = (item.Macro, item.Profile, item.DisplayPath);
        DialogResult  = true;
    }
}
