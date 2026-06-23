using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Services;
using ARK.UI.Views;
using WpfApp              = System.Windows.Application;
using WpfMessageBox       = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage  = System.Windows.MessageBoxImage;
using WpfMessageBoxResult = System.Windows.MessageBoxResult;
using WpfOpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using WpfSaveFileDialog   = Microsoft.Win32.SaveFileDialog;

namespace ARK.UI.ViewModels;

public sealed class MacroExplorerViewModel : ViewModelBase
{
    private readonly IStorageManager _storageManager;

    // ── Состояние навигации ───────────────────────────────────────────────────

    private SystemMap                    _tree         = new();
    private IReadOnlyList<MacroManifest> _allManifests = [];

    // Стек навигации: (Id папки или null=Root, имя)
    private readonly List<(Guid? FolderId, string Name)> _navStack = [];

    private Guid? CurrentFolderId => _navStack.Count > 0 ? _navStack[^1].FolderId : null;

    // Корневой AppFolderNode для текущей навигации (первый элемент стека)
    private AppFolderNode? CurrentAppFolder
    {
        get
        {
            if (_navStack.Count == 0) return null;
            var id = _navStack[0].FolderId;
            return id.HasValue ? FindNodeById(_tree.Roots, id.Value) as AppFolderNode : null;
        }
    }

    // ── Выбранный элемент / Контекстная привязка ─────────────────────────────

    private ExplorerItem?  _selectedItem;
    private AppFolderNode? _cbFolder;

    private bool   _cbIsGlobal      = true;
    private string _cbTargetProcess = string.Empty;
    private string _cbTitleFilter   = string.Empty;
    private bool   _cbFocusRequired = false;

    // ── Публичные свойства ────────────────────────────────────────────────────

    /// <summary>Пользователь открыл макрос из проводника — загружаем в Blueprint.</summary>
    public event Action<MacroDocument>? MacroOpenRequested;

    public ObservableCollection<ExplorerItem>   Items              { get; } = [];
    public ObservableCollection<BreadcrumbItem> Breadcrumbs        { get; } = [];
    public ObservableCollection<ProcessInfo>    ProcessSuggestions { get; } = [];

    public bool IsEmpty        => Items.Count == 0;
    public bool IsAtRoot       => _navStack.Count == 0;
    public bool IsInsideFolder => _navStack.Count > 0;

    // Панель привязки видна только ВНУТРИ AppFolder (не при выборе на корне)
    public bool IsContextBindingVisible => CurrentAppFolder is not null;

    public bool CbIsGlobal
    {
        get => _cbIsGlobal;
        set { if (SetProperty(ref _cbIsGlobal, value)) OnPropertyChanged(nameof(CbIsProcessVisible)); }
    }

    public bool   CbIsProcessVisible => !_cbIsGlobal;

    public string CbTargetProcess
    {
        get => _cbTargetProcess;
        set => SetProperty(ref _cbTargetProcess, value);
    }

    public string CbTitleFilter
    {
        get => _cbTitleFilter;
        set => SetProperty(ref _cbTitleFilter, value);
    }

    public bool CbFocusRequired
    {
        get => _cbFocusRequired;
        set => SetProperty(ref _cbFocusRequired, value);
    }

    // ── Команды ──────────────────────────────────────────────────────────────

    public ICommand CreateAppFolderCommand    { get; }
    public ICommand CreateFolderCommand       { get; }
    public ICommand CreateMacroCommand        { get; }
    public ICommand NavigateIntoCommand       { get; }
    public ICommand NavigateBackCommand       { get; }
    public ICommand EditMacroCommand          { get; }
    public ICommand PromoteCommand            { get; }
    public ICommand DemoteCommand             { get; }
    public ICommand DuplicateMacroCommand     { get; }
    public ICommand ExportMacroCommand        { get; }
    public ICommand ImportMacroCommand        { get; }
    public ICommand DeleteItemCommand         { get; }
    public ICommand RenameItemCommand         { get; }
    public ICommand CreateSubFolderCommand    { get; }
    public ICommand CreateMacroHereCommand    { get; }
    public ICommand SelectItemCommand         { get; }
    public ICommand SaveContextBindingCommand { get; }
    public ICommand LoadProcessesCommand      { get; }
    public ICommand RefreshCommand            { get; }

    public MacroExplorerViewModel(IStorageManager storageManager, IConfigService configService)
    {
        _storageManager = storageManager;

        CreateAppFolderCommand    = new AsyncRelayCommand(async _ => await CreateAppFolderAsync(), _ => IsAtRoot);
        CreateFolderCommand       = new AsyncRelayCommand(async _ => await CreateFolderAsync(CurrentFolderId), _ => IsInsideFolder);
        CreateMacroCommand        = new AsyncRelayCommand(async _ => await CreateMacroAsync(CurrentFolderId), _ => IsInsideFolder);
        NavigateIntoCommand       = new RelayCommand(NavigateInto);
        NavigateBackCommand       = new RelayCommand(_ => NavigateBack(), _ => _navStack.Count > 0);
        EditMacroCommand          = new AsyncRelayCommand(EditMacroAsync,
                                        p => p is ExplorerItem { Kind: ExplorerItemKind.Macro });
        PromoteCommand            = new AsyncRelayCommand(PromoteAsync,
                                        p => p is ExplorerItem { Kind: ExplorerItemKind.Macro, IsRelease: false });
        DemoteCommand             = new AsyncRelayCommand(DemoteAsync,
                                        p => p is ExplorerItem { Kind: ExplorerItemKind.Macro, IsRelease: true });
        DuplicateMacroCommand     = new AsyncRelayCommand(DuplicateAsync,
                                        p => p is ExplorerItem { Kind: ExplorerItemKind.Macro });
        ExportMacroCommand        = new AsyncRelayCommand(ExportMacroAsync,
                                        p => p is ExplorerItem { Kind: ExplorerItemKind.Macro });
        ImportMacroCommand        = new AsyncRelayCommand(async _ => await ImportMacroAsync());
        DeleteItemCommand         = new AsyncRelayCommand(DeleteItemAsync);
        RenameItemCommand         = new AsyncRelayCommand(RenameItemAsync);
        CreateSubFolderCommand    = new AsyncRelayCommand(
                                        p => CreateFolderAsync(GetFolderIdFromItem(p as ExplorerItem)),
                                        p => p is ExplorerItem { Kind: ExplorerItemKind.AppFolder or ExplorerItemKind.Folder });
        CreateMacroHereCommand    = new AsyncRelayCommand(
                                        p => CreateMacroAsync(GetFolderIdFromItem(p as ExplorerItem)),
                                        p => p is ExplorerItem { Kind: ExplorerItemKind.AppFolder or ExplorerItemKind.Folder });
        SelectItemCommand         = new RelayCommand(SelectItem);
        SaveContextBindingCommand = new AsyncRelayCommand(async _ => await SaveContextBindingAsync());
        LoadProcessesCommand      = new AsyncRelayCommand(async _ => await LoadProcessesAsync());
        RefreshCommand            = new AsyncRelayCommand(async _ => await LoadAsync());

        _ = LoadAsync();
    }

    // ── Загрузка данных ───────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        _tree         = await _storageManager.GetVirtualTreeAsync().ConfigureAwait(false);
        _allManifests = await _storageManager.GetAllMacrosAsync().ConfigureAwait(false);
        WpfApp.Current?.Dispatcher.Invoke(RefreshItems);
    }

    // ── Навигация ─────────────────────────────────────────────────────────────

    private void NavigateInto(object? param)
    {
        if (param is not ExplorerItem item) return;
        switch (item)
        {
            case { Kind: ExplorerItemKind.AppFolder, DataObject: AppFolderNode app }:
                _navStack.Add((app.Id, app.Name));
                LoadContextBindingFromCurrentAppFolder();
                RefreshItems();
                CommandManager.InvalidateRequerySuggested();
                break;
            case { Kind: ExplorerItemKind.Folder, DataObject: FolderNode folder }:
                _navStack.Add((folder.Id, folder.Name));
                RefreshItems();
                CommandManager.InvalidateRequerySuggested();
                break;
            case { Kind: ExplorerItemKind.Macro, DataObject: MacroManifest manifest }:
                _ = OpenMacroAsync(manifest);
                break;
        }
    }

    private void NavigateBack()
    {
        if (_navStack.Count > 0) _navStack.RemoveAt(_navStack.Count - 1);
        _selectedItem = null;
        LoadContextBindingFromCurrentAppFolder();
        RefreshItems();
        CommandManager.InvalidateRequerySuggested();
    }

    private void SelectItem(object? param)
    {
        _selectedItem = param as ExplorerItem;
        CommandManager.InvalidateRequerySuggested();
    }

    // Загружает настройки привязки из корневого AppFolder текущей навигации
    private void LoadContextBindingFromCurrentAppFolder()
    {
        var app          = CurrentAppFolder;
        _cbFolder        = app;
        _cbIsGlobal      = app?.Binding.IsGlobal      ?? true;
        _cbTargetProcess = app?.Binding.TargetProcess  ?? string.Empty;
        _cbTitleFilter   = app?.Binding.TitleFilter    ?? string.Empty;
        _cbFocusRequired = app?.Binding.FocusRequired  ?? false;
        OnPropertyChanged(nameof(IsAtRoot));
        OnPropertyChanged(nameof(IsInsideFolder));
        OnPropertyChanged(nameof(IsContextBindingVisible));
        OnPropertyChanged(nameof(CbIsGlobal));
        OnPropertyChanged(nameof(CbIsProcessVisible));
        OnPropertyChanged(nameof(CbTargetProcess));
        OnPropertyChanged(nameof(CbTitleFilter));
        OnPropertyChanged(nameof(CbFocusRequired));
    }

    // ── Построение Items ──────────────────────────────────────────────────────

    private void RefreshItems()
    {
        Items.Clear();

        if (CurrentFolderId is null)
        {
            foreach (var node in _tree.Roots)
                Items.Add(MakeTreeNodeItem(node));

            var categorized = CollectAllMacroIds(_tree.Roots);
            foreach (var m in _allManifests.Where(m => !categorized.Contains(m.Id)))
                Items.Add(MakeMacroItem(m));
        }
        else
        {
            var currentNode = FindNodeById(_tree.Roots, CurrentFolderId.Value);
            var (children, macroIds) = currentNode switch
            {
                AppFolderNode app => (app.Children, app.MacroIds),
                FolderNode f      => (f.Children,   f.MacroIds),
                _                 => ((List<VirtualTreeNode>)[], (List<Guid>)[])
            };

            foreach (var child in children)
                Items.Add(MakeTreeNodeItem(child));

            foreach (var id in macroIds)
            {
                var m = _allManifests.FirstOrDefault(x => x.Id == id);
                if (m is not null) Items.Add(MakeMacroItem(m));
            }
        }

        RebuildBreadcrumbs();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static ExplorerItem MakeTreeNodeItem(VirtualTreeNode node) => node switch
    {
        AppFolderNode app => new() { Kind = ExplorerItemKind.AppFolder, Name = app.Name,    DataObject = app },
        FolderNode f      => new() { Kind = ExplorerItemKind.Folder,    Name = f.Name,      DataObject = f },
        _                 => new() { Kind = ExplorerItemKind.Folder,    Name = node.Name,   DataObject = node }
    };

    private static ExplorerItem MakeMacroItem(MacroManifest m) => new()
    {
        Kind = ExplorerItemKind.Macro, Name = m.Name, DataObject = m,
        Environment = m.Environment, Priority = m.QueuePriority, CanSetPriority = true, IsEnabled = true
    };

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbItem
        {
            Label = "Макросы",
            NavigateToCmd = new RelayCommand(_ =>
            {
                _navStack.Clear(); _selectedItem = null;
                LoadContextBindingFromCurrentAppFolder();
                RefreshItems();
                CommandManager.InvalidateRequerySuggested();
            })
        });
        for (int i = 0; i < _navStack.Count; i++)
        {
            var idx  = i;
            var name = _navStack[i].Name;
            Breadcrumbs.Add(new BreadcrumbItem
            {
                Label = name,
                NavigateToCmd = new RelayCommand(_ =>
                {
                    while (_navStack.Count > idx + 1) _navStack.RemoveAt(_navStack.Count - 1);
                    _selectedItem = null;
                    LoadContextBindingFromCurrentAppFolder();
                    RefreshItems();
                    CommandManager.InvalidateRequerySuggested();
                })
            });
        }
    }

    // ── Открытие макроса ──────────────────────────────────────────────────────

    private async Task OpenMacroAsync(MacroManifest manifest)
    {
        try
        {
            var doc = await _storageManager.LoadMacroAsync(manifest.Id).ConfigureAwait(false);
            WpfApp.Current?.Dispatcher.Invoke(() => MacroOpenRequested?.Invoke(doc));
        }
        catch (Exception ex) { ShowError(ex.Message, "Ошибка загрузки"); }
    }

    private async Task EditMacroAsync(object? param)
    {
        if (param is ExplorerItem { Kind: ExplorerItemKind.Macro, DataObject: MacroManifest m })
            await OpenMacroAsync(m).ConfigureAwait(false);
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    private async Task CreateAppFolderAsync()
    {
        var name = PromptName(MakeUniqueName("Новая программа"));
        if (name is null) return;
        try
        {
            await _storageManager.AddFolderAsync(name, isAppFolder: true).ConfigureAwait(false);
            await LoadAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) { ShowError(ex.Message, "Дубликат имени"); }
        catch (Exception ex)                 { ShowError(ex.Message, "Ошибка создания"); }
    }

    private async Task CreateFolderAsync(Guid? parentFolderId)
    {
        var name = PromptName(MakeUniqueName("Новая папка"));
        if (name is null) return;
        try
        {
            await _storageManager.AddFolderAsync(name, isAppFolder: false, parentFolderId: parentFolderId)
                .ConfigureAwait(false);
            await LoadAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) { ShowError(ex.Message, "Дубликат имени"); }
        catch (Exception ex)                 { ShowError(ex.Message, "Ошибка создания"); }
    }

    private async Task CreateMacroAsync(Guid? targetFolderId)
    {
        var name = PromptName(MakeUniqueName("Новый макрос"));
        if (name is null) return;
        if (Items.Any(it => it.Kind == ExplorerItemKind.Macro
                         && it.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError($"Макрос с именем «{name}» уже существует в этой папке.", "Дубликат имени");
            return;
        }
        try
        {
            var doc = new MacroDocument { Id = Guid.NewGuid(), UserDefinedName = name, Environment = "beta" };
            await _storageManager.SaveMacroAsync(doc).ConfigureAwait(false);
            if (targetFolderId.HasValue)
                await _storageManager.MoveMacroToFolderAsync(doc.Id, targetFolderId.Value).ConfigureAwait(false);
            await LoadAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) { ShowError(ex.Message, "Дубликат имени"); }
        catch (Exception ex)                 { ShowError(ex.Message, "Ошибка создания"); }
    }

    private async Task DeleteItemAsync(object? param)
    {
        if (param is not ExplorerItem item) return;
        bool confirmed = false;
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            confirmed = WpfMessageBox.Show(
                $"Удалить «{item.Name}»? Действие необратимо.", "Подтверждение",
                WpfMessageBoxButton.YesNo, WpfMessageBoxImage.Question) == WpfMessageBoxResult.Yes;
        });
        if (!confirmed) return;
        try
        {
            switch (item)
            {
                case { Kind: ExplorerItemKind.Macro, DataObject: MacroManifest m }:
                    await _storageManager.DeleteMacroAsync(m.Id).ConfigureAwait(false);
                    break;
                case { Kind: ExplorerItemKind.AppFolder or ExplorerItemKind.Folder, DataObject: VirtualTreeNode n }:
                    await _storageManager.DeleteFolderAsync(n.Id).ConfigureAwait(false);
                    break;
            }
            await LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { ShowError(ex.Message, "Ошибка удаления"); }
    }

    private async Task RenameItemAsync(object? param)
    {
        if (param is not ExplorerItem item) return;
        string? newName = null;
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            var dlg = new RenameDialog(item.Name) { Owner = WpfApp.Current.MainWindow };
            if (dlg.ShowDialog() == true) newName = dlg.ResultText;
        });
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;
        try
        {
            switch (item)
            {
                case { Kind: ExplorerItemKind.Macro, DataObject: MacroManifest m }:
                    if (Items.Any(it => it.Kind == ExplorerItemKind.Macro
                                     && it != item
                                     && it.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                    {
                        ShowError($"Макрос с именем «{newName}» уже существует в этой папке.", "Дубликат имени");
                        return;
                    }
                    var doc = await _storageManager.LoadMacroAsync(m.Id).ConfigureAwait(false);
                    doc.UserDefinedName = newName;
                    await _storageManager.SaveMacroAsync(doc).ConfigureAwait(false);
                    break;
                case { Kind: ExplorerItemKind.AppFolder or ExplorerItemKind.Folder, DataObject: VirtualTreeNode n }:
                    await _storageManager.RenameFolderAsync(n.Id, newName).ConfigureAwait(false);
                    break;
            }
            await LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { ShowError(ex.Message, "Ошибка переименования"); }
    }

    // ── Promote / Demote ──────────────────────────────────────────────────────

    private async Task PromoteAsync(object? param)
    {
        if (param is not ExplorerItem { Kind: ExplorerItemKind.Macro, DataObject: MacroManifest m }) return;
        try { await _storageManager.PromoteToReleaseAsync(m.Id).ConfigureAwait(false); await LoadAsync().ConfigureAwait(false); }
        catch (Exception ex) { ShowError(ex.Message, "Ошибка Promote"); }
    }

    private async Task DemoteAsync(object? param)
    {
        if (param is not ExplorerItem { Kind: ExplorerItemKind.Macro, DataObject: MacroManifest m }) return;
        try { await _storageManager.DemoteToBetaAsync(m.Id).ConfigureAwait(false); await LoadAsync().ConfigureAwait(false); }
        catch (Exception ex) { ShowError(ex.Message, "Ошибка Demote"); }
    }

    // ── Дублирование ─────────────────────────────────────────────────────────

    private async Task DuplicateAsync(object? param)
    {
        if (param is not ExplorerItem { Kind: ExplorerItemKind.Macro, DataObject: MacroManifest m }) return;
        try { await _storageManager.DuplicateMacroAsync(m.Id, CurrentFolderId).ConfigureAwait(false); await LoadAsync().ConfigureAwait(false); }
        catch (Exception ex) { ShowError(ex.Message, "Ошибка дублирования"); }
    }

    // ── Экспорт / Импорт (.arkmacro) ─────────────────────────────────────────

    private async Task ExportMacroAsync(object? param)
    {
        if (param is not ExplorerItem { Kind: ExplorerItemKind.Macro, DataObject: MacroManifest m }) return;
        string? path = null;
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            var dlg = new WpfSaveFileDialog
                { Title = "Экспорт макроса", Filter = "ARK Macro (*.arkmacro)|*.arkmacro", FileName = m.Name, DefaultExt = ".arkmacro" };
            if (dlg.ShowDialog() == true) path = dlg.FileName;
        });
        if (path is null) return;
        try
        {
            await _storageManager.ExportMacroAsync(m.Id, path).ConfigureAwait(false);
            WpfApp.Current?.Dispatcher.Invoke(() =>
                WpfMessageBox.Show($"Экспортирован: {path}", "Экспорт", WpfMessageBoxButton.OK, WpfMessageBoxImage.Information));
        }
        catch (Exception ex) { ShowError(ex.Message, "Ошибка экспорта"); }
    }

    private async Task ImportMacroAsync()
    {
        string? path = null;
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            var dlg = new WpfOpenFileDialog
                { Title = "Импорт макроса", Filter = "ARK Macro (*.arkmacro)|*.arkmacro|JSON (*.json)|*.json", DefaultExt = ".arkmacro" };
            if (dlg.ShowDialog() == true) path = dlg.FileName;
        });
        if (path is null) return;
        try
        {
            var doc = await _storageManager.ImportMacroAsync(path, CurrentFolderId).ConfigureAwait(false);
            await LoadAsync().ConfigureAwait(false);
            WpfApp.Current?.Dispatcher.Invoke(() =>
                WpfMessageBox.Show($"Импортирован: {doc.UserDefinedName}", "Импорт", WpfMessageBoxButton.OK, WpfMessageBoxImage.Information));
        }
        catch (Exception ex) { ShowError(ex.Message, "Ошибка импорта"); }
    }

    // ── Панель контекстной привязки ───────────────────────────────────────────

    private async Task SaveContextBindingAsync()
    {
        if (_cbFolder is null) return;
        var binding = new ContextBinding
        {
            BindingType   = _cbIsGlobal ? "GLOBAL" : "FOCUS",
            TargetProcess = _cbTargetProcess,
            TitleFilter   = _cbTitleFilter,
            FocusRequired = _cbFocusRequired
        };
        try
        {
            await _storageManager.UpdateAppFolderBindingAsync(_cbFolder.Id, binding).ConfigureAwait(false);
            await LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { ShowError(ex.Message, "Ошибка сохранения привязки"); }
    }

    private async Task LoadProcessesAsync()
    {
        var list = await Task.Run(() =>
            System.Diagnostics.Process.GetProcesses()
                .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle))
                .Select(p => { var exe = TryGetMainModulePath(p); return new ProcessInfo(p.ProcessName, exe, ExtractProcessIcon(exe)); })
                .OrderBy(p => p.ProcessName)
                .ToList()).ConfigureAwait(false);

        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            ProcessSuggestions.Clear();
            foreach (var p in list) ProcessSuggestions.Add(p);
        });
    }

    private static string TryGetMainModulePath(System.Diagnostics.Process p)
    {
        try   { return p.MainModule?.FileName ?? string.Empty; }
        catch { return string.Empty; }
    }

    // ── Вспомогательные ──────────────────────────────────────────────────────

    // Генерирует уникальное имя для текущего уровня: "База" → "База (1)" → "База (2)" ...
    private string MakeUniqueName(string baseName)
    {
        var taken = Items.Select(it => it.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (int i = 1; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
        return baseName; // не достижимо на практике
    }

    private string? PromptName(string defaultName)
    {
        string? result = null;
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            var dlg = new RenameDialog(defaultName) { Owner = WpfApp.Current.MainWindow };
            if (dlg.ShowDialog() == true) result = dlg.ResultText;
        });
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static void ShowError(string msg, string title)
        => WpfApp.Current?.Dispatcher.Invoke(() =>
            WpfMessageBox.Show(msg, title, WpfMessageBoxButton.OK, WpfMessageBoxImage.Error));

    private static Guid? GetFolderIdFromItem(ExplorerItem? item)
        => item?.DataObject is VirtualTreeNode n ? n.Id : null;

    private static VirtualTreeNode? FindNodeById(List<VirtualTreeNode> nodes, Guid id)
    {
        foreach (var n in nodes)
        {
            if (n.Id == id) return n;
            var found = n switch
            {
                AppFolderNode app => FindNodeById(app.Children, id),
                FolderNode f      => FindNodeById(f.Children, id),
                _                 => null
            };
            if (found is not null) return found;
        }
        return null;
    }

    private static HashSet<Guid> CollectAllMacroIds(List<VirtualTreeNode> nodes)
    {
        var result = new HashSet<Guid>();
        foreach (var n in nodes)
        {
            switch (n)
            {
                case AppFolderNode app: result.UnionWith(app.MacroIds); result.UnionWith(CollectAllMacroIds(app.Children)); break;
                case FolderNode f:      result.UnionWith(f.MacroIds);   result.UnionWith(CollectAllMacroIds(f.Children));   break;
            }
        }
        return result;
    }

    // ── Иконки Shell ─────────────────────────────────────────────────────────

    private static BitmapSource? ExtractProcessIcon(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var info = new Win32Api.SHFILEINFO();
        var hr   = Win32Api.SHGetFileInfoW(path, 0, ref info,
            (uint)Marshal.SizeOf<Win32Api.SHFILEINFO>(),
            Win32Api.SHGFI_ICON | Win32Api.SHGFI_SMALLICON);
        if (hr == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
        finally { Win32Api.DestroyIcon(info.hIcon); }
    }
}
