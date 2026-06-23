using System.Windows;
using System.Windows.Controls;
using ARK.UI.ViewModels;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace ARK.UI.Views;

public partial class QueueSettingsControl
{
    public QueueSettingsControl() => InitializeComponent();

    // ── Двойной клик: провалиться в регион ───────────────────────────────────

    private void OnListMouseDoubleClick(object sender, WpfMouseButtonEventArgs e)
    {
        if (DataContext is not QueueViewModel vm) return;
        if (vm.SelectedItem is RegionListItemVm regionVm)
            vm.DrillInto(regionVm);
    }

    // ── ПКМ на пустом фоне: динамическое меню ────────────────────────────────

    private void OnCreateRegionMenuClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not QueueViewModel vm) return;
        vm.AddRegionCommand.Execute(null);
    }

    private void OnAddMacroContextMenuClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not QueueViewModel vm) return;
        vm.AddMacroCommand.Execute(null);
    }

    // ── ПКМ на регионе ────────────────────────────────────────────────────────

    private static RegionListItemVm? GetRegionVm(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu { Tag: RegionListItemVm vm } }) return vm;
        return null;
    }

    private void OnDrillIntoMenuClick(object sender, RoutedEventArgs e)
    {
        if (GetRegionVm(sender) is not { } regionVm) return;
        if (DataContext is not QueueViewModel vm) return;
        vm.SelectedItem = regionVm;
        vm.DrillInto(regionVm);
    }

    private void OnRenameRegionMenuClick(object sender, RoutedEventArgs e)
    {
        if (GetRegionVm(sender) is not { } regionVm) return;
        if (DataContext is not QueueViewModel vm) return;
        vm.SelectedItem = regionVm;
        vm.RenameRegionCommand.Execute(null);
    }

    private void OnDeleteRegionMenuClick(object sender, RoutedEventArgs e)
    {
        if (GetRegionVm(sender) is not { } regionVm) return;
        if (DataContext is not QueueViewModel vm) return;
        vm.SelectedItem = regionVm;
        vm.DeleteRegionCommand.Execute(null);
    }

    // ── ПКМ на макросе ────────────────────────────────────────────────────────

    private static MacroQueueItemVm? GetMacroVm(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu { Tag: MacroQueueItemVm vm } }) return vm;
        return null;
    }

    private void OnSetPriorityMenuClick(object sender, RoutedEventArgs e)
    {
        if (GetMacroVm(sender) is not { } macroVm) return;
        if (DataContext is not QueueViewModel vm) return;
        vm.SelectedItem = macroVm;
        vm.SetPriority(macroVm);
    }

    private void OnRemoveMacroMenuClick(object sender, RoutedEventArgs e)
    {
        if (GetMacroVm(sender) is not { } macroVm) return;
        if (DataContext is not QueueViewModel vm) return;
        vm.SelectedItem = macroVm;
        vm.RemoveMacroCommand.Execute(null);
    }
}
