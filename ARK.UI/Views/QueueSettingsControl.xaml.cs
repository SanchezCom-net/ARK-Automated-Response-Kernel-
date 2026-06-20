using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ARK.UI.ViewModels;

namespace ARK.UI.Views;

public partial class QueueSettingsControl
{
    public QueueSettingsControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // ── DataContext lifecycle ─────────────────────────────────────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is QueueViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;

        if (e.NewValue is QueueViewModel newVm)
            newVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueueViewModel.SelectedExecutionMode))
            SyncRadioButtons();
    }

    private void SyncRadioButtons()
    {
        if (DataContext is not QueueViewModel vm) return;
        var mode = vm.SelectedExecutionMode;

        _suppressModeChange = true;
        try
        {
            RadioStrict.IsChecked     = mode == "StrictQueue";
            RadioConcurrent.IsChecked = mode == "Concurrent";
        }
        finally
        {
            _suppressModeChange = false;
        }
    }

    // ── TreeView: выбор узла ──────────────────────────────────────────────

    private void OnTreeItemSelected(object sender, RoutedEventArgs e)
    {
        if (DataContext is not QueueViewModel vm) return;
        if (sender is not TreeViewItem item) return;

        e.Handled = true;

        switch (item.DataContext)
        {
            case QueueRegionNodeVm regionVm:
                vm.SelectedRegion    = regionVm.Region;
                vm.SelectedFolder    = null;
                vm.SelectedMacroNode = null;
                break;

            case QueueFolderNodeVm folderVm:
                vm.SelectedRegion    = folderVm.ParentRegion;
                vm.SelectedFolder    = folderVm.Folder;
                vm.SelectedMacroNode = null;
                break;

            case QueueMacroRefNodeVm macroVm:
                vm.SelectedRegion    = macroVm.ParentRegion;
                vm.SelectedFolder    = null;
                vm.SelectedMacroNode = macroVm;
                break;
        }
    }

    // ── Контекстное меню макроса ──────────────────────────────────────────

    private void OnSetPriorityMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        if (menuItem.Parent is not ContextMenu menu) return;
        if (menu.Tag is not QueueMacroRefNodeVm macroVm) return;
        if (DataContext is not QueueViewModel vm) return;

        vm.SelectedMacroNode = macroVm;
        vm.SetPriority(macroVm);
    }

    private void OnRemoveMacroMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        if (menuItem.Parent is not ContextMenu menu) return;
        if (menu.Tag is not QueueMacroRefNodeVm macroVm) return;
        if (DataContext is not QueueViewModel vm) return;

        vm.SelectedMacroNode = macroVm;
        vm.RemoveMacroCommand.Execute(null);
    }

    // ── RadioButton: режим выполнения ─────────────────────────────────────

    private bool _suppressModeChange;

    private void OnStrictQueueChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressModeChange) return;
        if (DataContext is QueueViewModel vm)
            vm.SelectedExecutionMode = "StrictQueue";
    }

    private void OnConcurrentChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressModeChange) return;
        if (DataContext is QueueViewModel vm)
            vm.SelectedExecutionMode = "Concurrent";
    }
}
