using System.Collections.ObjectModel;
using System.Windows.Input;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Services;
using ARK.UI.Views;
using WpfApp              = System.Windows.Application;
using WpfMessageBox       = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage  = System.Windows.MessageBoxImage;
using WpfMessageBoxResult = System.Windows.MessageBoxResult;

namespace ARK.UI.ViewModels;

public sealed class QueueViewModel : ViewModelBase
{
    private readonly IQueueService   _queueService;
    private readonly IProfileService _profileService;

    // ── VM-дерево регионов ───────────────────────────────────────────────
    public ObservableCollection<QueueRegionNodeVm> RegionNodes { get; } = [];

    // ── Выбранные элементы ───────────────────────────────────────────────
    private QueueRegion?         _selectedRegion;
    private QueueFolder?         _selectedFolder;
    private QueueMacroRefNodeVm? _selectedMacroNode;

    public QueueStore Store => _queueService.Store;

    public QueueRegion? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (SetProperty(ref _selectedRegion, value))
            {
                _selectedFolder    = null;
                _selectedMacroNode = null;
                OnPropertyChanged(nameof(SelectedFolder));
                OnPropertyChanged(nameof(IsRegionSelected));
                OnPropertyChanged(nameof(IsFolderSelected));
                OnPropertyChanged(nameof(IsMacroSelected));
                OnPropertyChanged(nameof(SelectedExecutionMode));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public QueueFolder? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (SetProperty(ref _selectedFolder, value))
            {
                _selectedMacroNode = null;
                OnPropertyChanged(nameof(IsFolderSelected));
                OnPropertyChanged(nameof(IsMacroSelected));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public QueueMacroRefNodeVm? SelectedMacroNode
    {
        get => _selectedMacroNode;
        set
        {
            if (SetProperty(ref _selectedMacroNode, value))
            {
                _selectedFolder = null;
                OnPropertyChanged(nameof(IsFolderSelected));
                OnPropertyChanged(nameof(IsMacroSelected));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsRegionSelected => _selectedRegion is not null;
    public bool IsFolderSelected => _selectedFolder is not null;
    public bool IsMacroSelected  => _selectedMacroNode is not null;

    public string SelectedExecutionMode
    {
        get => _selectedRegion?.ExecutionMode ?? "StrictQueue";
        set
        {
            if (_selectedRegion is null || _selectedRegion.ExecutionMode == value) return;
            _selectedRegion.ExecutionMode = value;
            OnPropertyChanged();
            _ = _queueService.SaveAsync();
        }
    }

    // ── Команды ───────────────────────────────────────────────────────────

    public ICommand AddRegionCommand    { get; }
    public ICommand AddFolderCommand    { get; }
    public ICommand AddMacroCommand     { get; }
    public ICommand RemoveMacroCommand  { get; }
    public ICommand SetPriorityCommand  { get; }
    public ICommand RenameItemCommand   { get; }
    public ICommand DeleteItemCommand   { get; }
    public ICommand SaveCommand         { get; }

    public QueueViewModel(IQueueService queueService, IProfileService profileService)
    {
        _queueService   = queueService;
        _profileService = profileService;

        AddRegionCommand   = new RelayCommand(_ => AddRegion());
        AddFolderCommand   = new RelayCommand(_ => AddFolder(),   _ => _selectedRegion is not null);
        AddMacroCommand    = new RelayCommand(_ => AddMacro(),    _ => _selectedRegion is not null);
        RemoveMacroCommand = new RelayCommand(_ => RemoveMacro(), _ => _selectedMacroNode is not null);
        SetPriorityCommand = new RelayCommand(_ => SetPriority(), _ => _selectedMacroNode is not null);
        RenameItemCommand  = new RelayCommand(_ => RenameSelected(),
                                              _ => _selectedRegion is not null || _selectedFolder is not null);
        DeleteItemCommand  = new RelayCommand(_ => DeleteSelected(),
                                              _ => _selectedRegion is not null || _selectedFolder is not null);
        SaveCommand        = new AsyncRelayCommand(async _ => await _queueService.SaveAsync());

        _queueService.Store.Regions.CollectionChanged += (_, _) => RebuildTree();
        RebuildTree();
    }

    // ── Построение VM-дерева (ленивая загрузка) ──────────────────────────

    public void RebuildTree()
    {
        RegionNodes.Clear();
        foreach (var region in _queueService.Store.Regions)
        {
            var r       = region;  // capture для замыкания
            var regionVm = new QueueRegionNodeVm(region, () => BuildRegionChildren(r));
            RegionNodes.Add(regionVm);
        }
    }

    // Строит дочерние узлы региона — вызывается только при раскрытии узла.
    private IEnumerable<QueueNodeVm> BuildRegionChildren(QueueRegion region)
    {
        foreach (var (macro, profile, path) in GetAllMacrosWithPaths())
            if (macro.RegionId == region.Id)
                yield return new QueueMacroRefNodeVm(macro, profile, region, path);
        foreach (var folder in region.Folders)
            yield return BuildFolderVm(folder, region);
    }

    private QueueFolderNodeVm BuildFolderVm(QueueFolder folder, QueueRegion region)
    {
        var f = folder;  // capture для замыкания
        return new QueueFolderNodeVm(folder, region, () => BuildFolderChildren(f, region));
    }

    // Строит дочерние узлы папки — вызывается только при раскрытии папки.
    private static IEnumerable<QueueNodeVm> BuildFolderChildren(QueueFolder folder, QueueRegion region)
    {
        foreach (var sub in folder.SubFolders)
            yield return new QueueFolderNodeVm(sub, region);
    }

    private IEnumerable<(MacroEntry Macro, AppProfile Profile, string Path)> GetAllMacrosWithPaths()
    {
        foreach (var p in _profileService.Profiles)
        {
            foreach (var m in p.Macros)
                yield return (m, p, p.FriendlyName);
            foreach (var f in p.Folders)
                foreach (var item in GetFromFolder(f, p, p.FriendlyName))
                    yield return item;
        }
    }

    private static IEnumerable<(MacroEntry, AppProfile, string)> GetFromFolder(
        VisualFolder f, AppProfile p, string parentPath)
    {
        var path = $"{parentPath} / {f.Name}";
        foreach (var m in f.Macros)
            yield return (m, p, path);
        foreach (var sub in f.SubFolders)
            foreach (var item in GetFromFolder(sub, p, path))
                yield return item;
    }

    // ── Добавить регион ───────────────────────────────────────────────────

    private void AddRegion()
    {
        var dialog = new RenameDialog("Новый регион") { Owner = WpfApp.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        if (!_queueService.TryAddRegion(dialog.ResultText ?? string.Empty, out var region, out var error))
        {
            WpfMessageBox.Show(error, "Ошибка", WpfMessageBoxButton.OK, WpfMessageBoxImage.Warning);
            return;
        }
        SelectedRegion = region;
        _ = _queueService.SaveAsync();
    }

    // ── Добавить папку ─────────────────────────────────────────────────────

    private void AddFolder()
    {
        if (_selectedRegion is null) return;

        var dialog = new RenameDialog("Новая папка") { Owner = WpfApp.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        if (!_queueService.TryAddFolder(_selectedRegion, _selectedFolder, dialog.ResultText ?? string.Empty,
                out var folder, out var error))
        {
            WpfMessageBox.Show(error, "Ошибка", WpfMessageBoxButton.OK, WpfMessageBoxImage.Warning);
            return;
        }
        SelectedFolder = folder;
        _ = _queueService.SaveAsync();
        RebuildTree();
    }

    // ── Добавить макрос в регион ──────────────────────────────────────────

    private void AddMacro()
    {
        if (_selectedRegion is null) return;

        var allMacros = GetAllMacrosWithPaths().ToList();
        var dialog    = new AddMacroToQueueDialog(allMacros) { Owner = WpfApp.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.SelectedMacro is null) return;

        var (macro, profile, _) = dialog.SelectedMacro.Value;
        macro.RegionId = _selectedRegion.Id;
        _ = _profileService.SaveProfileAsync(profile);
        RebuildTree();
    }

    // ── Убрать макрос из очереди ──────────────────────────────────────────

    private void RemoveMacro()
    {
        if (_selectedMacroNode is null) return;

        var res = WpfMessageBox.Show(
            $"Убрать «{_selectedMacroNode.Macro.Name}» из очереди?\nМакрос останется в профиле.",
            "Подтверждение", WpfMessageBoxButton.YesNo, WpfMessageBoxImage.Question);
        if (res != WpfMessageBoxResult.Yes) return;

        _selectedMacroNode.Macro.RegionId = null;
        _ = _profileService.SaveProfileAsync(_selectedMacroNode.Profile);
        SelectedMacroNode = null;
        RebuildTree();
    }

    // ── Задать приоритет ──────────────────────────────────────────────────

    public void SetPriority(QueueMacroRefNodeVm? targetNode = null)
    {
        var node = targetNode ?? _selectedMacroNode;
        if (node is null) return;

        var dialog = new PriorityDialog(node.QueuePriority) { Owner = WpfApp.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        node.QueuePriority = dialog.ResultPriority;
        _ = _profileService.SaveProfileAsync(node.Profile);
    }

    // ── Переименовать выбранный элемент ───────────────────────────────────

    private void RenameSelected()
    {
        if (_selectedFolder is not null && _selectedRegion is not null)
        {
            var dialog = new RenameDialog(_selectedFolder.Name) { Owner = WpfApp.Current.MainWindow };
            if (dialog.ShowDialog() != true) return;

            var (parent, found) = QueueService.FindFolder(_selectedRegion, _selectedFolder);
            if (!found) return;

            if (!_queueService.TryRenameFolder(_selectedFolder, parent, _selectedRegion,
                    dialog.ResultText ?? string.Empty, out var error))
            {
                WpfMessageBox.Show(error, "Ошибка", WpfMessageBoxButton.OK, WpfMessageBoxImage.Warning);
                return;
            }
            _ = _queueService.SaveAsync();
            RebuildTree();
            return;
        }

        if (_selectedRegion is not null)
        {
            var dialog = new RenameDialog(_selectedRegion.Name) { Owner = WpfApp.Current.MainWindow };
            if (dialog.ShowDialog() != true) return;

            if (!_queueService.TryRenameRegion(_selectedRegion,
                    dialog.ResultText ?? string.Empty, out var error))
            {
                WpfMessageBox.Show(error, "Ошибка", WpfMessageBoxButton.OK, WpfMessageBoxImage.Warning);
                return;
            }
            _ = _queueService.SaveAsync();
            RebuildTree();
        }
    }

    // ── Удалить выбранный элемент ──────────────────────────────────────────

    private void DeleteSelected()
    {
        if (_selectedFolder is not null && _selectedRegion is not null)
        {
            var res = WpfMessageBox.Show(
                $"Удалить папку «{_selectedFolder.Name}»?",
                "Подтверждение", WpfMessageBoxButton.YesNo, WpfMessageBoxImage.Question);
            if (res != WpfMessageBoxResult.Yes) return;

            var (parent, _) = QueueService.FindFolder(_selectedRegion, _selectedFolder);
            _queueService.DeleteFolder(_selectedRegion, parent, _selectedFolder);
            SelectedFolder = null;
            _ = _queueService.SaveAsync();
            RebuildTree();
            return;
        }

        if (_selectedRegion is not null)
        {
            var res = WpfMessageBox.Show(
                $"Удалить регион «{_selectedRegion.Name}»?\nМакросы, привязанные к этому региону, перестанут выполняться в очереди.",
                "Подтверждение", WpfMessageBoxButton.YesNo, WpfMessageBoxImage.Question);
            if (res != WpfMessageBoxResult.Yes) return;

            _queueService.DeleteRegion(_selectedRegion);
            SelectedRegion = null;
            _ = _queueService.SaveAsync();
        }
    }
}
