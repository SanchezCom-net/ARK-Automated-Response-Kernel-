using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Services;
using ARK.UI.Resources;
using ARK.UI.Views;
using WpfMessageBox       = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage  = System.Windows.MessageBoxImage;
using WpfSaveFileDialog   = Microsoft.Win32.SaveFileDialog;
using WpfOpenFileDialog   = Microsoft.Win32.OpenFileDialog;

namespace ARK.UI.ViewModels;

public sealed class MacroExplorerViewModel : ViewModelBase
{
    private enum NavLevel { Root, InProfile, InFolder, InRegion }

    private static readonly JsonSerializerOptions _cloneOptions = new()
    {
        PropertyNameCaseInsensitive     = true,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate
    };

    private readonly IConfigService     _configService;
    private readonly IProfileService    _profileService;
    private NavLevel                    _level;
    private AppProfile?                 _currentProfile;
    private readonly List<VisualFolder> _folderPath = new();
    private ProfileRegion?              _currentRegion;
    private string                      _currentPath = "Макросы";
    private ProcessInfo?                _selectedProcess;
    private MacroEntry?                 _copiedMacro;

    private VisualFolder? CurrentFolder => _folderPath.Count > 0 ? _folderPath[^1] : null;

    public event Action<AppProfile, ProfileRegion?, MacroEntry>? MacroOpenRequested;

    public ObservableCollection<AppProfile>     AllProfiles        => _profileService.Profiles;
    public ObservableCollection<ExplorerItem>   Items              { get; } = new();
    public ObservableCollection<BreadcrumbItem> Breadcrumbs        { get; } = new();
    public ObservableCollection<ProcessInfo>    ProcessSuggestions { get; } = new();

    public string CurrentPath
    {
        get => _currentPath;
        private set => SetProperty(ref _currentPath, value);
    }

    public bool IsEmpty                  => Items.Count == 0;
    public bool IsProfileSettingsVisible => _level == NavLevel.InProfile && _currentProfile is not null;
    public bool CanCreateProgram         => _level == NavLevel.Root;
    public bool CanPaste                 => _copiedMacro is not null;

    public ProcessInfo? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (ReferenceEquals(_selectedProcess, value)) return;
            _selectedProcess = value;
            if (_currentProfile is not null && value is not null)
                _currentProfile.TargetProcessName = value.ProcessName;
            OnPropertyChanged();
        }
    }

    // ── Прокси к свойствам активного профиля ──────────────────────────────

    public bool ProfileIsGlobal
    {
        get => _currentProfile?.IsGlobal ?? false;
        set
        {
            if (_currentProfile is null || _currentProfile.IsGlobal == value) return;
            _currentProfile.IsGlobal = value;
            OnPropertyChanged();
        }
    }

    public string ProfileTargetProcess
    {
        get => _currentProfile?.TargetProcessName ?? string.Empty;
        set
        {
            if (_currentProfile is null || _currentProfile.TargetProcessName == value) return;
            _currentProfile.TargetProcessName = value;
            OnPropertyChanged();
        }
    }

    public bool ProfileFocusRequired
    {
        get => _currentProfile?.FocusRequired ?? true;
        set
        {
            if (_currentProfile is null || _currentProfile.FocusRequired == value) return;
            _currentProfile.FocusRequired = value;
            OnPropertyChanged();
        }
    }

    public string ProfileWindowTitleFilter
    {
        get => _currentProfile?.WindowTitleFilter ?? string.Empty;
        set
        {
            if (_currentProfile is null || _currentProfile.WindowTitleFilter == value) return;
            _currentProfile.WindowTitleFilter = value;
            OnPropertyChanged();
        }
    }

    // ── Команды ────────────────────────────────────────────────────────────

    public ICommand NavigateIntoCommand       { get; }
    public ICommand NavigateBackCommand       { get; }
    public ICommand CreateProfileCommand      { get; }
    public ICommand CreateFolderCommand       { get; }
    public ICommand CreateMacroCommand        { get; }
    public ICommand DeleteItemCommand         { get; }
    public ICommand RenameItemCommand         { get; }
    public ICommand LoadProcessesCommand      { get; }
    public ICommand ToggleMacroEnabledCommand { get; }
    public ICommand SaveProfileCommand        { get; }
    public ICommand ExportProfileCommand      { get; }
    public ICommand ImportProfileCommand      { get; }
    public ICommand CopyMacroCommand          { get; }
    public ICommand PasteMacroCommand         { get; }
    public ICommand SetMacroPriorityCommand   { get; }

    public MacroExplorerViewModel(IConfigService configService, IProfileService profileService)
    {
        _configService  = configService;
        _profileService = profileService;

        NavigateIntoCommand       = new RelayCommand(NavigateInto);
        NavigateBackCommand       = new RelayCommand(_ => NavigateBack(),  _ => _level != NavLevel.Root);
        CreateProfileCommand      = new RelayCommand(_ => CreateProfile(), _ => _level == NavLevel.Root);
        CreateFolderCommand       = new RelayCommand(_ => CreateFolder(),  _ => _level is NavLevel.InProfile or NavLevel.InFolder);
        // CreateRegion убрана из вкладки Макросов — управление регионами только на вкладке Очередь.
        CreateMacroCommand        = new RelayCommand(_ => CreateMacro(),   _ => _level is NavLevel.InProfile or NavLevel.InFolder);
        DeleteItemCommand         = new RelayCommand(DeleteItem);
        RenameItemCommand         = new RelayCommand(RenameItem);
        LoadProcessesCommand      = new RelayCommand(_ => _ = LoadRunningProcessesAsync());
        ToggleMacroEnabledCommand = new RelayCommand(ToggleMacroEnabled);
        SaveProfileCommand        = new RelayCommand(_ => SaveCurrentProfile(),
                                                     _ => _currentProfile is not null);
        ExportProfileCommand      = new RelayCommand(_ => ExecuteExportFromUI(),
                                                     _ => _currentProfile is not null && _level >= NavLevel.InProfile);
        ImportProfileCommand      = new RelayCommand(_ => ExecuteImportFromUI());

        CopyMacroCommand = new RelayCommand(
            p => CopyMacro(p),
            p => p is ExplorerItem { Kind: ExplorerItemKind.Macro });

        PasteMacroCommand = new RelayCommand(
            _ => PasteMacro(),
            _ => _copiedMacro is not null
              && _level is NavLevel.InRegion or NavLevel.InProfile or NavLevel.InFolder);

        SetMacroPriorityCommand = new RelayCommand(
            p => SetMacroPriority(p),
            p => p is ExplorerItem { Kind: ExplorerItemKind.Macro, CanSetPriority: true });

        RefreshItems();
    }

    // ── Навигация ─────────────────────────────────────────────────────────

    private void NavigateInto(object? param)
    {
        if (param is not ExplorerItem item) return;

        switch (item.Kind)
        {
            case ExplorerItemKind.Profile when item.DataObject is AppProfile profile:
                _level           = NavLevel.InProfile;
                _currentProfile  = profile;
                _folderPath.Clear();
                _selectedProcess = null;
                break;

            case ExplorerItemKind.Folder when item.DataObject is VisualFolder folder:
                _folderPath.Add(folder);
                _level = NavLevel.InFolder;
                break;

            case ExplorerItemKind.Region when item.DataObject is ProfileRegion region:
                _level         = NavLevel.InRegion;
                _currentRegion = region;
                break;

            case ExplorerItemKind.Macro when item.DataObject is ProfileRegion directRegion:
                // Legacy: прямой регион (IsDirect=true) из старого профиля
                if (_currentProfile is not null && directRegion.Macros.Count > 0)
                    MacroOpenRequested?.Invoke(_currentProfile, directRegion, directRegion.Macros[0]);
                return;

            case ExplorerItemKind.Macro when item.DataObject is MacroEntry macroEntry:
                // Новая архитектура: макрос лежит напрямую в профиле/папке
                if (_currentProfile is not null)
                    MacroOpenRequested?.Invoke(_currentProfile, _currentRegion, macroEntry);
                return;
        }

        RefreshItems();
        CommandManager.InvalidateRequerySuggested();
    }

    private void NavigateBack()
    {
        switch (_level)
        {
            case NavLevel.InRegion:
                _currentRegion = null;
                _level = _folderPath.Count > 0 ? NavLevel.InFolder : NavLevel.InProfile;
                break;
            case NavLevel.InFolder:
                if (_folderPath.Count > 0)
                    _folderPath.RemoveAt(_folderPath.Count - 1);
                _level = _folderPath.Count > 0 ? NavLevel.InFolder : NavLevel.InProfile;
                break;
            case NavLevel.InProfile:
                _level           = NavLevel.Root;
                _currentProfile  = null;
                _selectedProcess = null;
                break;
        }
        RefreshItems();
        CommandManager.InvalidateRequerySuggested();
    }

    private void NavigateToRoot()
    {
        if (_level == NavLevel.Root) return;
        _level          = NavLevel.Root;
        _currentProfile = null;
        _currentRegion  = null;
        _folderPath.Clear();
        RefreshItems();
        CommandManager.InvalidateRequerySuggested();
    }

    private void NavigateToProfile()
    {
        if (_level is NavLevel.Root or NavLevel.InProfile) return;
        _folderPath.Clear();
        _currentRegion = null;
        _level = NavLevel.InProfile;
        RefreshItems();
        CommandManager.InvalidateRequerySuggested();
    }

    private void NavigateToFolderAt(int index)
    {
        if (index < 0 || index >= _folderPath.Count) return;
        _folderPath.RemoveRange(index + 1, _folderPath.Count - index - 1);
        _currentRegion = null;
        _level = NavLevel.InFolder;
        RefreshItems();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshItems()
    {
        Items.Clear();

        switch (_level)
        {
            case NavLevel.Root:
                foreach (var p in AllProfiles)
                {
                    var display = p.FriendlyName.Length > 0      ? p.FriendlyName
                                : p.TargetProcessName.Length > 0 ? p.TargetProcessName
                                : "Безымянная программа";
                    Items.Add(new ExplorerItem { Kind = ExplorerItemKind.Profile, Name = display, DataObject = p });
                }
                break;

            case NavLevel.InProfile when _currentProfile is not null:
                foreach (var f in _currentProfile.Folders)
                    Items.Add(new ExplorerItem { Kind = ExplorerItemKind.Folder, Name = f.Name, DataObject = f });
                // Новая архитектура: макросы напрямую в профиле
                foreach (var m in _currentProfile.Macros)
                    Items.Add(new ExplorerItem { Kind = ExplorerItemKind.Macro, Name = m.Name, DataObject = m,
                                                 IsEnabled = m.IsEnabled, Priority = m.QueuePriority,
                                                 CanSetPriority = true });
                // Обратная совместимость: прямые (IsDirect) регионы из старых профилей
                foreach (var r in _currentProfile.Regions.Where(r => r.IsDirect))
                    foreach (var m in r.Macros)
                        Items.Add(new ExplorerItem { Kind = ExplorerItemKind.Macro, Name = m.Name, DataObject = r,
                                                     IsEnabled = m.IsEnabled, Priority = m.QueuePriority });
                break;

            case NavLevel.InFolder when CurrentFolder is not null:
                foreach (var f in CurrentFolder.SubFolders)
                    Items.Add(new ExplorerItem { Kind = ExplorerItemKind.Folder, Name = f.Name, DataObject = f });
                // Новая архитектура: макросы напрямую в папке
                foreach (var m in CurrentFolder.Macros)
                    Items.Add(new ExplorerItem { Kind = ExplorerItemKind.Macro, Name = m.Name, DataObject = m,
                                                 IsEnabled = m.IsEnabled, Priority = m.QueuePriority,
                                                 CanSetPriority = true });
                // Обратная совместимость: прямые регионы из старых папок
                foreach (var r in CurrentFolder.Regions.Where(r => r.IsDirect))
                    foreach (var m in r.Macros)
                        Items.Add(new ExplorerItem { Kind = ExplorerItemKind.Macro, Name = m.Name, DataObject = r,
                                                     IsEnabled = m.IsEnabled, Priority = m.QueuePriority });
                break;

            case NavLevel.InRegion when _currentRegion is not null:
                // Сохраняется для обратной совместимости со старыми профилями
                foreach (var m in _currentRegion.Macros)
                    Items.Add(new ExplorerItem { Kind = ExplorerItemKind.Macro, Name = m.Name, DataObject = m,
                                                 IsEnabled = m.IsEnabled, Priority = m.QueuePriority,
                                                 CanSetPriority = true });
                break;
        }

        RebuildBreadcrumbs();
        NotifyProfileSettingsChanged();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanCreateProgram));

        if (IsProfileSettingsVisible)
            _ = LoadRunningProcessesAsync();
    }

    private void NotifyProfileSettingsChanged()
    {
        OnPropertyChanged(nameof(IsProfileSettingsVisible));
        OnPropertyChanged(nameof(ProfileIsGlobal));
        OnPropertyChanged(nameof(ProfileTargetProcess));
        OnPropertyChanged(nameof(ProfileFocusRequired));
        OnPropertyChanged(nameof(ProfileWindowTitleFilter));
        OnPropertyChanged(nameof(SelectedProcess));
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbItem
        {
            Label         = "Макросы",
            NavigateToCmd = new RelayCommand(_ => NavigateToRoot())
        });

        if (_level >= NavLevel.InProfile && _currentProfile is not null)
        {
            var name = _currentProfile.FriendlyName.Length > 0      ? _currentProfile.FriendlyName
                     : _currentProfile.TargetProcessName.Length > 0 ? _currentProfile.TargetProcessName
                     : "Безымянная программа";
            Breadcrumbs.Add(new BreadcrumbItem
            {
                Label         = name,
                NavigateToCmd = new RelayCommand(_ => NavigateToProfile())
            });
        }

        for (int i = 0; i < _folderPath.Count; i++)
        {
            var capturedIndex = i;
            Breadcrumbs.Add(new BreadcrumbItem
            {
                Label         = _folderPath[i].Name,
                NavigateToCmd = new RelayCommand(_ => NavigateToFolderAt(capturedIndex))
            });
        }

        if (_level == NavLevel.InRegion && _currentRegion is not null)
            Breadcrumbs.Add(new BreadcrumbItem
            {
                Label         = _currentRegion.RegionName,
                NavigateToCmd = new RelayCommand(_ => { })
            });

        CurrentPath = string.Join(" › ", Breadcrumbs.Select(b => b.Label));
    }

    // ── Включение / отключение макроса ────────────────────────────────────

    private void ToggleMacroEnabled(object? param)
    {
        if (param is not ExplorerItem { Kind: ExplorerItemKind.Macro } item) return;

        MacroEntry? entry = item.DataObject switch
        {
            MacroEntry m                                                    => m,
            ProfileRegion { IsDirect: true } dr when dr.Macros.Count > 0  => dr.Macros[0],
            _                                                               => null
        };
        if (entry is null) return;

        entry.IsEnabled = !entry.IsEnabled;
        item.IsEnabled  = entry.IsEnabled;

        SaveCurrentProfile();
    }

    // ── Загрузка запущенных процессов с иконками ──────────────────────────

    private async Task LoadRunningProcessesAsync()
    {
        // Сохраняем ДО async-части — на UI-потоке, пока значение ещё актуально.
        string savedProcess = _currentProfile?.TargetProcessName ?? string.Empty;

        var systemPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "svchost", "system", "registry", "smss", "csrss", "wininit",
            "winlogon", "lsass", "services", "conhost", "fontdrvhost",
            "runtimebroker", "dllhost", "sihost"
        };

        var entries = await Task.Run(() =>
        {
            var list = new List<(string Name, string Path)>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName;
                    if (systemPrefixes.Any(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    string path = string.Empty;
                    try { path = p.MainModule?.FileName ?? string.Empty; }
                    catch { /* недостаточно прав — пропускаем путь */ }

                    list.Add((name + ".exe", path));
                }
                catch { /* процесс завершился пока мы его читали */ }
            }
            return list
                .DistinctBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }).ConfigureAwait(false);

        // Извлечение иконок и обновление коллекции — строго на UI-потоке.
        // ProcessSuggestions.Clear() может сбросить SelectedItem в ComboBox и через
        // двусторонний Text binding затереть TargetProcessName. Поэтому ПОСЛЕ заполнения
        // принудительно восстанавливаем выбранный процесс через SelectedProcess.
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            ProcessSuggestions.Clear();
            foreach (var (name, path) in entries)
            {
                var icon = string.IsNullOrEmpty(path) ? null : ExtractProcessIcon(path);
                ProcessSuggestions.Add(new ProcessInfo(name, path, icon));
            }

            // Железное восстановление: ищем совпадение в загруженном списке.
            // Если процесс не запущен — создаём временный ProcessInfo без иконки,
            // чтобы имя не сбрасывалось в пустую строку.
            var match = ProcessSuggestions.FirstOrDefault(
                p => p.ProcessName.Equals(savedProcess, StringComparison.OrdinalIgnoreCase));
            SelectedProcess = match ?? (string.IsNullOrEmpty(savedProcess)
                ? null
                : new ProcessInfo(savedProcess, string.Empty, null));
        });
    }

    /// <summary>Извлекает маленькую иконку (16×16) из exe-файла через SHGetFileInfoW.</summary>
    public static BitmapSource? ExtractProcessIcon(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var info = new Win32Api.SHFILEINFO();
        var result = Win32Api.SHGetFileInfoW(
            path, 0, ref info,
            (uint)Marshal.SizeOf<Win32Api.SHFILEINFO>(),
            Win32Api.SHGFI_ICON | Win32Api.SHGFI_SMALLICON);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            Win32Api.DestroyIcon(info.hIcon);
        }
    }

    // ── Валидация уникальности имён ───────────────────────────────────────

    private IEnumerable<string> GetNamesInCurrentScope(object? excludeDataObject = null)
    {
        switch (_level)
        {
            case NavLevel.Root:
                foreach (var p in AllProfiles)
                {
                    if (ReferenceEquals(p, excludeDataObject)) continue;
                    yield return p.FriendlyName.Length > 0 ? p.FriendlyName : p.TargetProcessName;
                }
                break;

            case NavLevel.InProfile when _currentProfile is not null:
                foreach (var f in _currentProfile.Folders)
                {
                    if (ReferenceEquals(f, excludeDataObject)) continue;
                    yield return f.Name;
                }
                foreach (var m in _currentProfile.Macros)
                {
                    if (ReferenceEquals(m, excludeDataObject)) continue;
                    yield return m.Name;
                }
                break;

            case NavLevel.InFolder when CurrentFolder is not null:
                foreach (var f in CurrentFolder.SubFolders)
                {
                    if (ReferenceEquals(f, excludeDataObject)) continue;
                    yield return f.Name;
                }
                foreach (var m in CurrentFolder.Macros)
                {
                    if (ReferenceEquals(m, excludeDataObject)) continue;
                    yield return m.Name;
                }
                break;

            case NavLevel.InRegion when _currentRegion is not null:
                foreach (var m in _currentRegion.Macros)
                {
                    if (ReferenceEquals(m, excludeDataObject)) continue;
                    yield return m.Name;
                }
                break;
        }
    }

    private bool IsNameUniqueInCurrentScope(string name, object? excludeDataObject = null)
        => !GetNamesInCurrentScope(excludeDataObject)
               .Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    private static string GetUniqueNameFromSet(string baseName, HashSet<string> existing)
    {
        if (!existing.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    private string EnsureUniqueName(string baseName)
        => GetUniqueNameFromSet(
               baseName,
               GetNamesInCurrentScope().ToHashSet(StringComparer.OrdinalIgnoreCase));

    // ── Создание ─────────────────────────────────────────────────────────

    private void CreateProfile()
    {
        var existing = AllProfiles
            .Select(p => p.FriendlyName.Length > 0 ? p.FriendlyName : p.TargetProcessName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profile = new AppProfile
        {
            FriendlyName      = GetUniqueNameFromSet("Новая программа", existing),
            TargetProcessName = "process.exe"
        };
        AllProfiles.Add(profile);
        _ = _profileService.SaveProfileAsync(profile);
        RefreshItems();
    }

    private void CreateFolder()
    {
        var folder = new VisualFolder { Name = EnsureUniqueName("Новая папка") };
        if (_level == NavLevel.InProfile && _currentProfile is not null)
            _currentProfile.Folders.Add(folder);
        else if (_level == NavLevel.InFolder && CurrentFolder is not null)
            CurrentFolder.SubFolders.Add(folder);
        SaveCurrentProfile();
        RefreshItems();
    }

    private void CreateRegion()
    {
        var region = new ProfileRegion { RegionName = EnsureUniqueName("Новый регион") };
        if (_level == NavLevel.InProfile && _currentProfile is not null)
            _currentProfile.Regions.Add(region);
        else if (_level == NavLevel.InFolder && CurrentFolder is not null)
            CurrentFolder.Regions.Add(region);
        SaveCurrentProfile();
        RefreshItems();
    }

    private void CreateMacro()
    {
        var entry = new MacroEntry { Name = EnsureUniqueName("Новый макрос") };
        if (_level == NavLevel.InProfile && _currentProfile is not null)
        {
            _currentProfile.Macros.Add(entry);
        }
        else if (_level == NavLevel.InFolder && CurrentFolder is not null)
        {
            CurrentFolder.Macros.Add(entry);
        }
        else if (_level == NavLevel.InRegion && _currentRegion is not null)
        {
            // Обратная совместимость: если навигация попала в старый регион
            _currentRegion.Macros.Add(entry);
        }
        else return;
        SaveCurrentProfile();
        RefreshItems();
    }

    // ── Удаление ─────────────────────────────────────────────────────────

    private void DeleteItem(object? param)
    {
        if (param is not ExplorerItem item) return;

        switch (item.Kind)
        {
            case ExplorerItemKind.Profile when item.DataObject is AppProfile p:
                _ = _profileService.DeleteProfileAsync(p);
                break;

            case ExplorerItemKind.Folder when item.DataObject is VisualFolder f:
                if (_level == NavLevel.InProfile && _currentProfile is not null)
                    _currentProfile.Folders.Remove(f);
                else if (_level == NavLevel.InFolder && CurrentFolder is not null)
                    CurrentFolder.SubFolders.Remove(f);
                SaveCurrentProfile();
                break;

            case ExplorerItemKind.Region when item.DataObject is ProfileRegion r:
                if (_level == NavLevel.InProfile && _currentProfile is not null)
                    _currentProfile.Regions.Remove(r);
                else if (_level == NavLevel.InFolder && CurrentFolder is not null)
                    CurrentFolder.Regions.Remove(r);
                SaveCurrentProfile();
                break;

            case ExplorerItemKind.Macro when item.DataObject is ProfileRegion dr:
                if (_level == NavLevel.InProfile && _currentProfile is not null)
                    _currentProfile.Regions.Remove(dr);
                else if (_level == NavLevel.InFolder && CurrentFolder is not null)
                    CurrentFolder.Regions.Remove(dr);
                SaveCurrentProfile();
                break;

            case ExplorerItemKind.Macro when item.DataObject is MacroEntry m:
                // Новая архитектура: ищем макрос в профиле или папке
                if (_level == NavLevel.InProfile && _currentProfile is not null)
                    _currentProfile.Macros.Remove(m);
                else if (_level == NavLevel.InFolder && CurrentFolder is not null)
                    CurrentFolder.Macros.Remove(m);
                else if (_currentRegion is not null)
                    _currentRegion.Macros.Remove(m);
                SaveCurrentProfile();
                break;
        }
        RefreshItems();
    }

    // ── Переименование ────────────────────────────────────────────────────

    private void RenameItem(object? param)
    {
        if (param is not ExplorerItem item) return;
        var newName = ShowRenameDialog(item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;

        if (!IsNameUniqueInCurrentScope(newName, item.DataObject))
        {
            WpfMessageBox.Show(
                string.Format(Strings.Error_DuplicateNameMessage, newName),
                Strings.Error_DuplicateNameTitle,
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Warning);
            return;
        }

        switch (item.Kind)
        {
            case ExplorerItemKind.Profile when item.DataObject is AppProfile p:
                p.FriendlyName = newName;
                _ = _profileService.SaveProfileAsync(p);
                break;
            case ExplorerItemKind.Folder when item.DataObject is VisualFolder f:
                f.Name = newName;
                SaveCurrentProfile();
                break;
            case ExplorerItemKind.Region when item.DataObject is ProfileRegion r:
                r.RegionName = newName;
                SaveCurrentProfile();
                break;
            case ExplorerItemKind.Macro when item.DataObject is ProfileRegion dr:
                dr.RegionName = newName;
                if (dr.Macros.Count > 0) dr.Macros[0].Name = newName;
                SaveCurrentProfile();
                break;
            case ExplorerItemKind.Macro when item.DataObject is MacroEntry m:
                m.Name = newName;
                SaveCurrentProfile();
                break;
        }
        RefreshItems();
    }

    private static string? ShowRenameDialog(string current)
    {
        var dlg = new RenameDialog(current);
        return dlg.ShowDialog() == true ? dlg.ResultText : null;
    }

    private void SaveCurrentProfile()
    {
        if (_currentProfile is null) return;
        // Фиксируем имя процесса из SelectedProcess (последнего выбора из списка)
        // на случай если Text binding не успел обновить модель.
        if (_selectedProcess is not null)
            _currentProfile.TargetProcessName = _selectedProcess.ProcessName;
        _ = _profileService.SaveProfileAsync(_currentProfile);
    }

    // ── Экспорт / Импорт профилей (.ark) ─────────────────────────────────

    private void ExecuteExportFromUI()
    {
        if (_currentProfile is null) return;

        var saveDlg = new WpfSaveFileDialog
        {
            Title       = Strings.Export_PasswordTitle,
            Filter      = "Пакет ARK (*.ark)|*.ark",
            DefaultExt  = ".ark",
            FileName    = _currentProfile.FriendlyName.Length > 0
                          ? _currentProfile.FriendlyName
                          : _currentProfile.TargetProcessName
        };
        if (saveDlg.ShowDialog() != true) return;

        var pwdDlg = new PasswordDialog(Strings.Export_PasswordTitle, Strings.Export_PasswordPrompt);
        if (pwdDlg.ShowDialog() != true) return;

        var path     = saveDlg.FileName;
        var password = string.IsNullOrEmpty(pwdDlg.Password) ? null : pwdDlg.Password;
        var profile  = _currentProfile;

        _ = Task.Run(async () =>
        {
            try
            {
                await _profileService.ExportProfileAsync(profile, path, password).ConfigureAwait(false);
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    WpfMessageBox.Show(
                        string.Format(Strings.Export_SuccessMessage, path),
                        Strings.Export_SuccessTitle,
                        WpfMessageBoxButton.OK,
                        WpfMessageBoxImage.Information));
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    WpfMessageBox.Show(
                        ex.Message,
                        Strings.Error_ExportTitle,
                        WpfMessageBoxButton.OK,
                        WpfMessageBoxImage.Error));
            }
        });
    }

    private void ExecuteImportFromUI()
    {
        var openDlg = new WpfOpenFileDialog
        {
            Title      = Strings.Import_PasswordTitle,
            Filter     = "Пакет ARK (*.ark)|*.ark",
            DefaultExt = ".ark"
        };
        if (openDlg.ShowDialog() != true) return;

        var path = openDlg.FileName;
        _ = Task.Run(() => TryImportAsync(path, null));
    }

    private async Task TryImportAsync(string path, string? password)
    {
        try
        {
            var profile = await _profileService.ImportProfileAsync(path, password).ConfigureAwait(false);

            await System.Windows.Application.Current!.Dispatcher.InvokeAsync(() =>
            {
                var existing = AllProfiles.FirstOrDefault(p => p.Id == profile.Id);
                if (existing is not null) AllProfiles.Remove(existing);
                AllProfiles.Add(profile);
                RefreshItems();
                WpfMessageBox.Show(
                    string.Format(Strings.Import_SuccessMessage, profile.FriendlyName),
                    Strings.Import_SuccessTitle,
                    WpfMessageBoxButton.OK,
                    WpfMessageBoxImage.Information);
            });
        }
        catch (CryptographicException) when (password is null)
        {
            string? newPassword = null;
            var cancelled = false;
            await System.Windows.Application.Current!.Dispatcher.InvokeAsync(() =>
            {
                var pwdDlg = new PasswordDialog(Strings.Import_PasswordTitle, Strings.Import_PasswordPrompt);
                if (pwdDlg.ShowDialog() != true) { cancelled = true; return; }
                newPassword = string.IsNullOrEmpty(pwdDlg.Password) ? string.Empty : pwdDlg.Password;
            });
            if (!cancelled)
                await TryImportAsync(path, newPassword ?? string.Empty).ConfigureAwait(false);
        }
        catch (CryptographicException)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                WpfMessageBox.Show(
                    Strings.Error_WrongPassword,
                    Strings.Error_ImportTitle,
                    WpfMessageBoxButton.OK,
                    WpfMessageBoxImage.Warning));
        }
        catch (Exception ex)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                WpfMessageBox.Show(
                    ex.Message,
                    Strings.Error_ImportTitle,
                    WpfMessageBoxButton.OK,
                    WpfMessageBoxImage.Error));
        }
    }

    // ── Копирование / Вставка макросов ─────────────────────────────────────

    private void CopyMacro(object? param)
    {
        MacroEntry? source = param switch
        {
            ExplorerItem { DataObject: MacroEntry m }                                    => m,
            ExplorerItem { DataObject: ProfileRegion { IsDirect: true } dr }
                when dr.Macros.Count > 0                                                  => dr.Macros[0],
            _                                                                             => null
        };
        if (source is null) return;

        _copiedMacro = DeepClone(source);
        OnPropertyChanged(nameof(CanPaste));
        CommandManager.InvalidateRequerySuggested();
    }

    private void PasteMacro()
    {
        if (_copiedMacro is null) return;

        var clone = DeepClone(_copiedMacro);

        // Авто-инкремент имени: "Имя (1)" → "Имя (2)" → ...
        var baseName = clone.Name;
        var counter  = 1;
        var rxMatch  = Regex.Match(baseName, @"\((?<num>\d+)\)$");
        if (rxMatch.Success)
        {
            baseName = baseName[..rxMatch.Index].TrimEnd();
            counter  = int.Parse(rxMatch.Groups["num"].Value) + 1;
        }

        var existingNames = GetNamesInCurrentScope().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newName       = $"{baseName} ({counter})";
        while (existingNames.Contains(newName))
        {
            counter++;
            newName = $"{baseName} ({counter})";
        }
        clone.Name = newName;

        if (_level == NavLevel.InRegion && _currentRegion is not null)
        {
            _currentRegion.Macros.Add(clone);
        }
        else if (_level == NavLevel.InProfile && _currentProfile is not null)
        {
            var dr = new ProfileRegion { RegionName = clone.Name, ExecutionMode = "Concurrent", IsDirect = true };
            dr.Macros.Add(clone);
            _currentProfile.Regions.Add(dr);
        }
        else if (_level == NavLevel.InFolder && CurrentFolder is not null)
        {
            var dr = new ProfileRegion { RegionName = clone.Name, ExecutionMode = "Concurrent", IsDirect = true };
            dr.Macros.Add(clone);
            CurrentFolder.Regions.Add(dr);
        }
        else return;

        SaveCurrentProfile();
        RefreshItems();
    }

    // ── Задать приоритет в очереди ─────────────────────────────────────────

    private void SetMacroPriority(object? param)
    {
        if (param is not ExplorerItem { DataObject: MacroEntry macro }) return;

        var dlg = new RenameDialog(macro.QueuePriority.ToString());
        if (dlg.ShowDialog() != true) return;

        if (!int.TryParse(dlg.ResultText?.Trim(), out var priority) || priority < 0) return;
        macro.QueuePriority = priority;

        SaveCurrentProfile();
        RefreshItems();
    }

    // ── Глубокое клонирование MacroEntry с генерацией новых GUID ──────────

    private static MacroEntry DeepClone(MacroEntry source)
    {
        var json = JsonSerializer.Serialize(source, _cloneOptions);

        // Заменяем ID самого MacroEntry и всех нод на новые GUID.
        // NodeId == LogicalNode.Id по конструктору — одна замена покрывает
        // NodeId, Id ноды, OnSuccessNodeId/OnErrorNodeId, StartNodeId и MacroEntry.Id.
        var replacements = new Dictionary<string, string>
        {
            [source.Id.ToString()] = Guid.NewGuid().ToString()
        };
        foreach (var vn in source.VisualNodes)
        {
            if (!replacements.ContainsKey(vn.NodeId.ToString()))
                replacements[vn.NodeId.ToString()] = Guid.NewGuid().ToString();
        }

        foreach (var (oldId, newId) in replacements)
            json = json.Replace(oldId, newId, StringComparison.OrdinalIgnoreCase);

        return JsonSerializer.Deserialize<MacroEntry>(json, _cloneOptions)!;
    }
}
