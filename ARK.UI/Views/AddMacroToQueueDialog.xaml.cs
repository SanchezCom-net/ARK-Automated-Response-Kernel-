using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ARK.UI.Core.Models;
using WpfKeyEventArgs         = System.Windows.Input.KeyEventArgs;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace ARK.UI.Views;

public partial class AddMacroToQueueDialog : Window
{
    public sealed record MacroDisplayItem(string Name, string DisplayPath, string Environment, MacroManifest Manifest)
    {
        public bool IsRelease => Environment.Equals("release", StringComparison.OrdinalIgnoreCase);
    }

    public (MacroManifest Manifest, string Path)? SelectedMacro { get; private set; }

    private readonly List<MacroDisplayItem> _allItems;
    private ICollectionView?               _view;

    public AddMacroToQueueDialog(IEnumerable<(MacroManifest Manifest, string Path)> allMacros)
    {
        _allItems = allMacros
            .Select(t => new MacroDisplayItem(t.Manifest.Name, t.Path, t.Manifest.Environment, t.Manifest))
            .OrderBy(i => i.Name)
            .ToList();

        InitializeComponent();

        // ICollectionView — live-фильтрация без пересоздания коллекции
        var cvs = new CollectionViewSource { Source = _allItems };
        _view = cvs.View;
        _view.Filter = FilterMacro;
        MacroList.ItemsSource = _view;

        Loaded += (_, _) => SearchBox.Focus();
    }

    private bool FilterMacro(object obj)
    {
        if (obj is not MacroDisplayItem item) return false;
        var text = SearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return true;
        return item.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || item.DisplayPath.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        // Автовыбор если единственный результат
        var hits = _view?.Cast<MacroDisplayItem>().Take(2).ToList();
        MacroList.SelectedIndex = hits?.Count == 1 ? 0 : -1;
    }

    private void OnTitleBarDrag(object sender, WpfMouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnAddClick(object sender, RoutedEventArgs e) => Confirm();

    private void OnListDoubleClick(object sender, WpfMouseButtonEventArgs e)
    {
        if (MacroList.SelectedItem is MacroDisplayItem) Confirm();
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
        SelectedMacro = (item.Manifest, item.DisplayPath);
        DialogResult  = true;
    }
}
