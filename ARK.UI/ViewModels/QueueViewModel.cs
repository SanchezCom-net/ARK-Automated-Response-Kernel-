using System.Collections.ObjectModel;
using System.Windows.Input;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Views;
using WpfApp              = System.Windows.Application;
using WpfMessageBox       = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage  = System.Windows.MessageBoxImage;
using WpfMessageBoxResult = System.Windows.MessageBoxResult;

namespace ARK.UI.ViewModels;

public sealed class QueueViewModel : ViewModelBase
{
    private readonly IQueueManager   _queueManager;
    private readonly IStorageManager _storageManager;

    private Dictionary<Guid, MacroManifest> _manifestCache = new();
    private SystemMap?                       _systemMap;

    // ── Данные списка ─────────────────────────────────────────────────────────

    public ObservableCollection<QueueListItemVm> DisplayItems { get; } = [];

    private QueueListItemVm? _selectedItem;
    public QueueListItemVm? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    // Хелперы для CanExecute — определяют тип выбранного элемента
    private RegionListItemVm? SelectedRegionItem => _selectedItem as RegionListItemVm;
    private MacroQueueItemVm? SelectedMacroItem  => _selectedItem as MacroQueueItemVm;

    // ── Навигация (drill-down) ────────────────────────────────────────────────

    private RegionQueue? _currentRegion;

    public RegionQueue? CurrentRegion  => _currentRegion;
    public bool IsAtRoot               => _currentRegion is null;
    public bool IsInsideRegion         => _currentRegion is not null;
    public string BreadcrumbText       => _currentRegion?.Name ?? string.Empty;

    public string EmptyStateText => IsAtRoot
        ? "Нет регионов. Нажмите «＋ Регион» или ПКМ для создания."
        : "Регион пуст. Нажмите «＋ Макрос» для добавления.";

    private void SetCurrentRegion(RegionQueue? value)
    {
        _currentRegion = value;
        OnPropertyChanged(nameof(CurrentRegion));
        OnPropertyChanged(nameof(IsAtRoot));
        OnPropertyChanged(nameof(IsInsideRegion));
        OnPropertyChanged(nameof(BreadcrumbText));
        OnPropertyChanged(nameof(EmptyStateText));
        _selectedItem = null;
        OnPropertyChanged(nameof(SelectedItem));
        CommandManager.InvalidateRequerySuggested();
    }

    // ── Команды ──────────────────────────────────────────────────────────────

    public ICommand AddRegionCommand    { get; }
    public ICommand AddMacroCommand     { get; }
    public ICommand RemoveMacroCommand  { get; }
    public ICommand SetPriorityCommand  { get; }
    public ICommand DeleteRegionCommand { get; }
    public ICommand RenameRegionCommand { get; }
    public ICommand ClearRegionCommand  { get; }
    public ICommand NavigateBackCommand { get; }
    public ICommand RefreshCommand      { get; }

    public QueueViewModel(IQueueManager queueManager, IStorageManager storageManager)
    {
        _queueManager   = queueManager;
        _storageManager = storageManager;

        AddRegionCommand    = new AsyncRelayCommand(
            async _ => await AddRegionAsync(),
            _        => IsAtRoot);

        AddMacroCommand     = new AsyncRelayCommand(
            async _ => await AddMacroAsync(),
            _        => IsInsideRegion);

        RemoveMacroCommand  = new AsyncRelayCommand(
            async _ => await RemoveMacroAsync(),
            _        => IsInsideRegion && SelectedMacroItem is not null);

        SetPriorityCommand  = new AsyncRelayCommand(
            async _ => await SetPriorityAsync(null),
            _        => IsInsideRegion && SelectedMacroItem is not null);

        DeleteRegionCommand = new AsyncRelayCommand(
            async _ => await DeleteRegionAsync(),
            _        => IsAtRoot && SelectedRegionItem is not null);

        RenameRegionCommand = new AsyncRelayCommand(
            async _ => await RenameRegionAsync(),
            _        => IsAtRoot && SelectedRegionItem is not null);

        ClearRegionCommand  = new AsyncRelayCommand(
            async _ => await ClearRegionAsync(),
            _        => IsInsideRegion);

        NavigateBackCommand = new AsyncRelayCommand(
            async _ => await NavigateBackAsync(),
            _        => IsInsideRegion);

        RefreshCommand      = new AsyncRelayCommand(
            async _ => await RefreshAsync());

        _ = RefreshAsync();
    }

    // ── Построение пути макроса из виртуального дерева ───────────────────────

    /// <summary>
    /// Возвращает родительский путь (без имени макроса): "Программа  ›  Папка".
    /// Пустая строка если макрос не в дереве.
    /// </summary>
    internal static string BuildLocationPath(SystemMap map, Guid macroId)
    {
        const string sep = "  ›  ";
        foreach (var root in map.Roots)
        {
            var result = FindInNode(root, macroId, [root.Name], sep);
            if (result is not null) return result;
        }
        return string.Empty;
    }

    private static string? FindInNode(
        VirtualTreeNode node, Guid macroId, List<string> segments, string sep)
    {
        IEnumerable<Guid>            macroIds;
        IEnumerable<VirtualTreeNode> children;

        if (node is AppFolderNode app)
        {
            macroIds = app.MacroIds;
            children = app.Children;
        }
        else if (node is FolderNode folder)
        {
            macroIds = folder.MacroIds;
            children = folder.Children;
        }
        else return null;

        if (macroIds.Contains(macroId))
            return string.Join(sep, segments);

        foreach (var child in children)
        {
            var next = new List<string>(segments) { child.Name };
            var hit  = FindInNode(child, macroId, next, sep);
            if (hit is not null) return hit;
        }
        return null;
    }

    // ── Загрузка данных ───────────────────────────────────────────────────────

    public async Task RefreshAsync()
    {
        var regions   = await _queueManager.GetAllRegionsAsync().ConfigureAwait(false);
        var systemMap = await _storageManager.GetVirtualTreeAsync().ConfigureAwait(false);
        var manifests = await _storageManager.GetAllMacrosAsync().ConfigureAwait(false);

        _systemMap     = systemMap;
        _manifestCache = manifests.ToDictionary(m => m.Id);

        // Ищем текущий регион в обновлённом списке (мог быть удалён)
        var currentMatch = _currentRegion is null
            ? null
            : regions.FirstOrDefault(r => r.RegionId == _currentRegion.RegionId);

        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            DisplayItems.Clear();

            if (currentMatch is null)
            {
                // Если текущий регион исчез — возвращаемся в корень
                if (_currentRegion is not null) SetCurrentRegion(null);
                foreach (var r in regions)
                    DisplayItems.Add(new RegionListItemVm(r));
            }
            else
            {
                _currentRegion = currentMatch;
                foreach (var item in BuildMacroItems(currentMatch, systemMap))
                    DisplayItems.Add(item);
            }

            CommandManager.InvalidateRequerySuggested();
        });
    }

    private IEnumerable<MacroQueueItemVm> BuildMacroItems(RegionQueue region, SystemMap map)
    {
        foreach (var entry in region.Entries
                     .OrderBy(e => e.Priority == 0 ? int.MaxValue : e.Priority))
        {
            if (!_manifestCache.TryGetValue(entry.MacroId, out var manifest)) continue;
            var loc = BuildLocationPath(map, entry.MacroId);
            yield return new MacroQueueItemVm(entry, manifest, region, loc);
        }
    }

    // ── Drill-down навигация ──────────────────────────────────────────────────

    public void DrillInto(RegionListItemVm regionItem)
    {
        SetCurrentRegion(regionItem.Region);

        var map = _systemMap ?? new SystemMap();
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            DisplayItems.Clear();
            foreach (var item in BuildMacroItems(regionItem.Region, map))
                DisplayItems.Add(item);
        });
    }

    private async Task NavigateBackAsync()
    {
        SetCurrentRegion(null);
        await RefreshAsync().ConfigureAwait(false);
    }

    // ── Добавить регион ───────────────────────────────────────────────────────

    private async Task AddRegionAsync()
    {
        string? name = null;
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            var dlg = new RenameDialog("Новый регион") { Owner = WpfApp.Current.MainWindow };
            if (dlg.ShowDialog() == true) name = dlg.ResultText;
        });
        if (string.IsNullOrWhiteSpace(name)) return;

        var region = new RegionQueue { Name = name };
        await _queueManager.SaveRegionAsync(region).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    // ── Добавить макрос (только Release) в текущий регион ────────────────────

    private async Task AddMacroAsync()
    {
        if (_currentRegion is null) return;

        var systemMap = _systemMap ?? await _storageManager.GetVirtualTreeAsync().ConfigureAwait(false);
        var manifests = await _storageManager.GetAllMacrosAsync().ConfigureAwait(false);

        // Все макросы (Beta + Release); путь = "Программа  ›  Папка" (без имени макроса)
        var allMacros = manifests
            .Select(m => (m, BuildLocationPath(systemMap, m.Id)))
            .ToList();

        (MacroManifest Manifest, string Path)? selected = null;
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            var dlg = new AddMacroToQueueDialog(allMacros) { Owner = WpfApp.Current.MainWindow };
            if (dlg.ShowDialog() == true) selected = dlg.SelectedMacro;
        });
        if (selected is null) return;

        await _queueManager.AddMacroToRegionAsync(
            _currentRegion.RegionId, selected.Value.Manifest.Id, 0).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    // ── Убрать выбранный макрос из региона ───────────────────────────────────

    private async Task RemoveMacroAsync()
    {
        if (SelectedMacroItem is not { } item) return;

        bool confirmed = false;
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            confirmed = WpfMessageBox.Show(
                $"Убрать «{item.Manifest.Name}» из очереди?",
                "Подтверждение", WpfMessageBoxButton.YesNo, WpfMessageBoxImage.Question)
                == WpfMessageBoxResult.Yes;
        });
        if (!confirmed) return;

        await _queueManager.RemoveMacroFromRegionAsync(
            item.Region.RegionId, item.Entry.MacroId).ConfigureAwait(false);
        SelectedItem = null;
        await RefreshAsync().ConfigureAwait(false);
    }

    // ── Задать приоритет + пересортировать ───────────────────────────────────

    public async Task SetPriorityAsync(MacroQueueItemVm? target)
    {
        var node = target ?? SelectedMacroItem;
        if (node is null) return;

        int? newPriority = null;
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            var dlg = new PriorityDialog(node.Priority) { Owner = WpfApp.Current.MainWindow };
            if (dlg.ShowDialog() == true) newPriority = dlg.ResultPriority;
        });
        if (newPriority is null) return;

        node.Priority = newPriority.Value;
        await _queueManager.UpdatePriorityAsync(
            node.Region.RegionId, node.Entry.MacroId, newPriority.Value).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    // Синхронный перегруз для code-behind (из ContextMenu)
    public void SetPriority(MacroQueueItemVm? target = null)
        => _ = SetPriorityAsync(target);

    // ── Очистить все макросы текущего региона ────────────────────────────────

    private async Task ClearRegionAsync()
    {
        if (_currentRegion is null) return;

        bool confirmed = false;
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            confirmed = WpfMessageBox.Show(
                $"Очистить регион «{_currentRegion.Name}»?\nВсе макросы будут удалены из очереди.",
                "Подтверждение", WpfMessageBoxButton.YesNo, WpfMessageBoxImage.Question)
                == WpfMessageBoxResult.Yes;
        });
        if (!confirmed) return;

        _currentRegion.Entries.Clear();
        await _queueManager.SaveRegionAsync(_currentRegion).ConfigureAwait(false);
        SelectedItem = null;
        await RefreshAsync().ConfigureAwait(false);
    }

    // ── Удалить выбранный регион ─────────────────────────────────────────────

    private async Task DeleteRegionAsync()
    {
        if (SelectedRegionItem is not { } item) return;

        bool confirmed = false;
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            confirmed = WpfMessageBox.Show(
                $"Удалить регион «{item.Region.Name}»?\nВсе записи в нём будут потеряны.",
                "Подтверждение", WpfMessageBoxButton.YesNo, WpfMessageBoxImage.Question)
                == WpfMessageBoxResult.Yes;
        });
        if (!confirmed) return;

        await _queueManager.DeleteRegionAsync(item.Region.RegionId).ConfigureAwait(false);
        SelectedItem = null;
        await RefreshAsync().ConfigureAwait(false);
    }

    // ── Переименовать выбранный регион ────────────────────────────────────────

    private async Task RenameRegionAsync()
    {
        if (SelectedRegionItem is not { } item) return;

        string? newName = null;
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            var dlg = new RenameDialog(item.Region.Name) { Owner = WpfApp.Current.MainWindow };
            if (dlg.ShowDialog() == true) newName = dlg.ResultText;
        });
        if (string.IsNullOrWhiteSpace(newName)) return;

        item.Region.Name = newName;
        await _queueManager.SaveRegionAsync(item.Region).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }
}
