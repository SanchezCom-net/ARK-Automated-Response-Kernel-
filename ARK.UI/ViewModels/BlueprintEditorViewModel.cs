using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using KokoroSharp;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Nodes;
using ARK.UI.Core.Nodes.OBS;
using ARK.UI.Core.Services;
using ARK.UI.Resources;

namespace ARK.UI.ViewModels;

public sealed class BlueprintEditorViewModel : ViewModelBase, IDisposable
{
    private readonly INodeEngine      _nodeEngine;
    private readonly ILogService      _logger;
    private readonly IProfileService  _profileService;
    private readonly IObsService      _obsService;
    private readonly IConfigService   _configService;
    private readonly IServiceProvider _serviceProvider;
    private VisualNode?              _selectedNode;
    private AppProfile?              _currentProfile;
    private ProfileRegion?           _currentRegion;
    private MacroEntry?              _currentMacro;
    private CancellationTokenSource? _debounceCts;

    public ObservableCollection<VisualNode>           VisualNodes           { get; } = new();
    public ObservableCollection<VisualConnection>     VisualConnections     { get; } = new();
    public ObservableCollection<VisualConnectionLine> ConnectionLines       { get; } = new();
    public ObservableCollection<string>               AvailableObsScenes    { get; } = new();
    public ObservableCollection<string>               AvailableSources      { get; } = new();
    public ObservableCollection<string>               AvailableFilters      { get; } = new();
    public ObservableCollection<string>               AvailableAudioSources { get; } = new();
    public ObservableCollection<string>               AvailableVoices       { get; } = new();
    public ObservableCollection<ProcessInfo>           ActiveSystemProcesses { get; } = new();

    public bool IsObsConnected => _obsService.IsConnected;
    public bool IsTtsDisabled  => _configService.Current.SelectedTtsMode == TtsMode.Disabled;

    private ProcessInfo? _selectedActiveProcess;
    public ProcessInfo? SelectedActiveProcess
    {
        get => _selectedActiveProcess;
        set
        {
            if (!SetProperty(ref _selectedActiveProcess, value) || value is null) return;

            if (_selectedNode?.LogicalNode is RunProcessNode rp)
            {
                if (IsWindowsSystemProcess(value.ProcessPath))
                {
                    rp.FilePathOrUrl = value.ProcessName;
                    rp.Arguments     = string.Empty;
                }
                else
                {
                    rp.FilePathOrUrl = value.ProcessPath;
                    rp.Arguments     = Path.GetFileName(value.ProcessPath);
                }
            }
            else if (_selectedNode?.LogicalNode is Win_ProcessManagerNode pm)
            {
                var empty = pm.ProcessesList.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.Text));
                if (empty is not null)
                    empty.Text = value.ProcessName;
            }
            else if (_selectedNode?.LogicalNode is Win_WindowManagerNode wm)
            {
                var empty = wm.ProcessesList.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.Text));
                if (empty is not null)
                    empty.Text = value.ProcessName;
            }
        }
    }

    private static bool IsWindowsSystemProcess(string processPath)
    {
        if (string.IsNullOrEmpty(processPath)) return true;
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return processPath.StartsWith(winDir, StringComparison.OrdinalIgnoreCase);
    }

    private string _nodeDebugLogs = string.Empty;
    public string NodeDebugLogs
    {
        get => _nodeDebugLogs;
        set => SetProperty(ref _nodeDebugLogs, value);
    }

    private bool _isDebugConsoleOpen;
    public bool IsDebugConsoleOpen
    {
        get => _isDebugConsoleOpen;
        set => SetProperty(ref _isDebugConsoleOpen, value);
    }

    public void AppendDebugLog(string message)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            NodeDebugLogs += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        });
    }

    public IReadOnlyList<NodeTemplateViewModel> NodeTemplates    { get; }
    public ICollectionView                      NodeTemplatesView { get; }
    public ICommand                             DropNodeCommand           { get; }
    public ICommand                             DeleteNodeCommand         { get; }
    public ICommand                             DeleteConnectionCommand   { get; }
    public ICommand                             SetAsStartNodeCommand     { get; }
    public ICommand                             TestNodeCommand               { get; }
    public ICommand                             TestChainCommand              { get; }
    public ICommand                             CloseDebugConsoleCommand      { get; }
    public ICommand                             CaptureCoordinatesCommand     { get; }

    public string ToolboxTitle    => Strings.ResourceManager.GetString("Toolbox_Header",   Strings.Culture) ?? "НОДЫ";
    public string ToolboxDragHint => Strings.ResourceManager.GetString("Toolbox_DragHint", Strings.Culture) ?? "Перетащите ноду на холст";

    public double ToolboxWidth    => _configService.Current.ToolboxWidth;
    public double PropertiesWidth => _configService.Current.PropertiesWidth;

    public VisualNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (_selectedNode is not null) _selectedNode.IsSelected = false;
            if (SetProperty(ref _selectedNode, value) && value is not null)
            {
                value.IsSelected = true;
                _ = RefreshObsDataForNodeAsync(value.LogicalNode);
                if (value.LogicalNode is Win_SpeakTextNode)
                {
                    RefreshAvailableVoices();
                    OnPropertyChanged(nameof(IsTtsDisabled));
                }
                if (value.LogicalNode is RunProcessNode or Win_WindowManagerNode)
                    _ = RefreshActiveProcessesAsync();
            }
        }
    }

    public BlueprintEditorViewModel(
        INodeEngine nodeEngine, ILogService logger,
        IProfileService profileService, IObsService obsService,
        IConfigService configService, IServiceProvider serviceProvider)
    {
        _nodeEngine      = nodeEngine;
        _logger          = logger;
        _profileService  = profileService;
        _obsService      = obsService;
        _configService   = configService;
        _serviceProvider = serviceProvider;
        _obsService.ConnectionStatusChanged += OnObsConnectionChanged;

        NodeTemplates = BuildNodeTemplates();
        RefreshAvailableVoices();

        var view = CollectionViewSource.GetDefaultView(NodeTemplates);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NodeTemplateViewModel.CategoryName)));
        NodeTemplatesView = view;

        LocalizationService.CultureChanged += OnCultureChanged;
        TestNodeCommand               = new RelayCommand(p => { _ = OnTestNodeAsync(); });
        TestChainCommand              = new RelayCommand(p => { _ = OnTestChainAsync(); });
        CloseDebugConsoleCommand      = new RelayCommand(_ => IsDebugConsoleOpen = false);
        CaptureCoordinatesCommand     = new RelayCommand(p => { if (p is MouseActionNode n) _ = OnCaptureCoordinatesAsync(n); });
        DropNodeCommand           = new RelayCommand<NodeDropPayload>(ExecuteDropNode);
        DeleteNodeCommand         = new RelayCommand(p => { if (p is VisualNode vn && vn.LogicalNode.IsRemovable) RemoveNode(vn.NodeId); });
        DeleteConnectionCommand   = new RelayCommand(p => { if (p is VisualConnectionLine line) DeleteConnection(line); });
        SetAsStartNodeCommand     = new RelayCommand(p =>
        {
            if (p is VisualNode vn && _currentMacro is not null)
            {
                _currentMacro.StartNodeId = vn.NodeId;
                SaveCurrent();
            }
        });

        double x = 50;
        foreach (var node in nodeEngine.Nodes)
        {
            AddVisualNode(node, x, 100);
            x += 350;
        }

        foreach (var vn in VisualNodes.ToList())
        {
            if (vn.LogicalNode.OnSuccessNodeId.HasValue)
                ConnectNodes(vn.NodeId, vn.LogicalNode.OnSuccessNodeId.Value, isErrorRoute: false);
            if (vn.LogicalNode.OnErrorNodeId.HasValue)
                ConnectNodes(vn.NodeId, vn.LogicalNode.OnErrorNodeId.Value, isErrorRoute: true);
        }
    }

    // ── Узлы ─────────────────────────────────────────────────────────────

    public VisualNode AddVisualNode(BaseNode node, double x, double y)
    {
        var vn = new VisualNode(node, x, y);
        VisualNodes.Add(vn);
        _currentMacro?.VisualNodes.Add(vn);
        SubscribeNode(vn);
        SaveCurrent();
        return vn;
    }

    public void ConnectNodes(Guid sourceId, Guid targetId, bool isErrorRoute, bool isDataRoute = false)
    {
        // Toggle: если такая связь уже существует — разрываем её
        var existingLine = ConnectionLines.FirstOrDefault(l =>
            l.Source.NodeId == sourceId &&
            l.Target.NodeId == targetId &&
            l.IsErrorRoute  == isErrorRoute &&
            l.IsDataRoute   == isDataRoute);

        if (existingLine is not null)
        {
            var srcName = VisualNodes.FirstOrDefault(n => n.NodeId == sourceId)?.LogicalNode.Name
                          ?? sourceId.ToString();
            var tgtName = VisualNodes.FirstOrDefault(n => n.NodeId == targetId)?.LogicalNode.Name
                          ?? targetId.ToString();
            _ = _logger.LogInfoAsync(nameof(BlueprintEditorViewModel),
                $"[КОНСТРУКТОР] Повторное перетаскивание провода. Разрываю и удаляю связь между нодами '{srcName}' и '{tgtName}'.");
            DeleteConnection(existingLine);
            return;
        }

        var sourceVn = VisualNodes.FirstOrDefault(n => n.NodeId == sourceId);
        var targetVn = VisualNodes.FirstOrDefault(n => n.NodeId == targetId);
        if (sourceVn is null || targetVn is null) return;

        var conn = new VisualConnection
        {
            SourceNodeId = sourceId,
            TargetNodeId = targetId,
            IsErrorRoute = isErrorRoute,
            IsDataRoute  = isDataRoute
        };
        VisualConnections.Add(conn);
        _currentMacro?.VisualConnections.Add(conn);
        ConnectionLines.Add(new VisualConnectionLine(sourceVn, targetVn, isErrorRoute, isDataRoute));

        if (isErrorRoute)
            sourceVn.LogicalNode.OnErrorNodeId   = targetId;
        else if (!isDataRoute)
            sourceVn.LogicalNode.OnSuccessNodeId = targetId;

        SaveCurrent();
    }

    public void RemoveNode(Guid nodeId)
    {
        var vn = VisualNodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (vn is null || !vn.LogicalNode.IsRemovable) return;

        UnsubscribeNode(vn);
        if (_selectedNode?.NodeId == nodeId) SelectedNode = null;

        VisualNodes.Remove(vn);
        _currentMacro?.VisualNodes.Remove(vn);

        var staleConns = VisualConnections
            .Where(c => c.SourceNodeId == nodeId || c.TargetNodeId == nodeId)
            .ToList();
        foreach (var c in staleConns)
        {
            VisualConnections.Remove(c);
            _currentMacro?.VisualConnections.Remove(c);
        }

        var staleLines = ConnectionLines
            .Where(l => l.Source.NodeId == nodeId || l.Target.NodeId == nodeId)
            .ToList();
        foreach (var l in staleLines)
        {
            l.Dispose();
            ConnectionLines.Remove(l);
        }

        SaveCurrent();
    }

    public void LoadFromMacro(AppProfile profile, ProfileRegion? region, MacroEntry macro)
    {
        ResetAllNodeStates();

        foreach (var vn in VisualNodes)
            UnsubscribeNode(vn);

        foreach (var line in ConnectionLines)
            line.Dispose();
        ConnectionLines.Clear();
        VisualConnections.Clear();
        VisualNodes.Clear();
        SelectedNode = null;

        _currentProfile = profile;
        _currentRegion  = region;
        _currentMacro   = macro;

        foreach (var vn in macro.VisualNodes)
        {
            VisualNodes.Add(vn);
            SubscribeNode(vn);
        }

        foreach (var vc in macro.VisualConnections)
        {
            VisualConnections.Add(vc);
            var src = VisualNodes.FirstOrDefault(n => n.NodeId == vc.SourceNodeId);
            var tgt = VisualNodes.FirstOrDefault(n => n.NodeId == vc.TargetNodeId);
            if (src is not null && tgt is not null)
                ConnectionLines.Add(new VisualConnectionLine(src, tgt, vc.IsErrorRoute, vc.IsDataRoute));
        }

        // Гарантируем ровно одну TriggerRootNode: авто-создаём для старых конфигов без неё
        var existingRoot = VisualNodes.FirstOrDefault(vn => vn.LogicalNode is TriggerRootNode);
        if (existingRoot is null)
        {
            var rootNode = new TriggerRootNode { Name = "▶ Старт" };
            var rootVn   = new VisualNode(rootNode, 50, 200);
            VisualNodes.Add(rootVn);
            macro.VisualNodes.Add(rootVn);
            SubscribeNode(rootVn);
            // Если StartNodeId не задан — назначаем TriggerRootNode точкой входа
            macro.StartNodeId ??= rootNode.Id;
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ToolboxTitle));
        OnPropertyChanged(nameof(ToolboxDragHint));
        NodeTemplatesView.Refresh();
    }

    public void Dispose()
    {
        LocalizationService.CultureChanged -= OnCultureChanged;
        _obsService.ConnectionStatusChanged -= OnObsConnectionChanged;
        _debounceCts?.Cancel();
        _debounceCts = null;
        foreach (var vn in VisualNodes)
            UnsubscribeNode(vn);
        foreach (var line in ConnectionLines)
            line.Dispose();
    }

    // ── OBS сцены ─────────────────────────────────────────────────────────

    private void OnObsConnectionChanged(object? sender, bool connected)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(IsObsConnected));
            if (!connected)
            {
                AvailableObsScenes.Clear();
                AvailableSources.Clear();
                AvailableFilters.Clear();
                AvailableAudioSources.Clear();
            }
        });
    }

    private async Task RefreshObsDataForNodeAsync(BaseNode node)
    {
        bool needsScenes = node is OBS_SceneManagerNode or OBS_SourceVisibilityManagerNode
                                or OBS_DynamicContentManagerNode or ObsSetSceneNode;
        if (needsScenes) await RefreshObsScenesAsync().ConfigureAwait(false);

        if (node is OBS_AudioManagerNode)
            await RefreshAudioSourcesAsync().ConfigureAwait(false);

        if (node is IObsCascadeNode cascade)
        {
            if (!string.IsNullOrEmpty(cascade.SelectedScene))
                await RefreshAvailableSourcesAsync(cascade.SelectedScene).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(cascade.SelectedSource) && node is OBS_SourceVisibilityManagerNode)
                await RefreshAvailableFiltersAsync(cascade.SelectedSource).ConfigureAwait(false);
        }
    }

    private async Task RefreshObsScenesAsync()
    {
        if (!_obsService.IsConnected)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(AvailableObsScenes.Clear);
            return;
        }
        try
        {
            var scenes = await _obsService.GetScenesAsync().ConfigureAwait(false);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                AvailableObsScenes.Clear();
                foreach (var s in scenes) AvailableObsScenes.Add(s);
            });
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(BlueprintEditorViewModel),
                $"[OBS] Не удалось загрузить список сцен: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task RefreshAvailableSourcesAsync(string? sceneName)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() => { AvailableSources.Clear(); AvailableFilters.Clear(); });
        if (!_obsService.IsConnected || string.IsNullOrEmpty(sceneName)) return;
        try
        {
            var sources = await _obsService.GetSceneInputsAsync(sceneName).ConfigureAwait(false);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var s in sources) AvailableSources.Add(s);
            });
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(BlueprintEditorViewModel),
                $"[OBS] Не удалось загрузить источники сцены '{sceneName}': {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task RefreshAvailableFiltersAsync(string? sourceName)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(AvailableFilters.Clear);
        if (!_obsService.IsConnected || string.IsNullOrEmpty(sourceName)) return;
        try
        {
            var filters = await _obsService.GetSourceFiltersAsync(sourceName).ConfigureAwait(false);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var f in filters) AvailableFilters.Add(f);
            });
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(BlueprintEditorViewModel),
                $"[OBS] Не удалось загрузить фильтры источника '{sourceName}': {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task RefreshAudioSourcesAsync()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(AvailableAudioSources.Clear);
        if (!_obsService.IsConnected) return;
        try
        {
            var inputs = await _obsService.GetAudioSourcesAsync().ConfigureAwait(false);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var i in inputs) AvailableAudioSources.Add(i);
            });
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(BlueprintEditorViewModel),
                $"[OBS] Не удалось загрузить список аудиовходов: {ex.Message}").ConfigureAwait(false);
        }
    }

    // ── DeleteConnectionCommand ───────────────────────────────────────────

    private void DeleteConnection(VisualConnectionLine line)
    {
        var conn = VisualConnections.FirstOrDefault(c =>
            c.SourceNodeId == line.Source.NodeId &&
            c.TargetNodeId == line.Target.NodeId &&
            c.IsErrorRoute == line.IsErrorRoute  &&
            c.IsDataRoute  == line.IsDataRoute);

        if (conn is not null)
        {
            VisualConnections.Remove(conn);
            _currentMacro?.VisualConnections.Remove(conn);
        }

        line.Dispose();
        ConnectionLines.Remove(line);

        var sourceVn = VisualNodes.FirstOrDefault(n => n.NodeId == line.Source.NodeId);
        if (sourceVn is not null)
        {
            if (line.IsErrorRoute)
                sourceVn.LogicalNode.OnErrorNodeId   = null;
            else if (!line.IsDataRoute)
                sourceVn.LogicalNode.OnSuccessNodeId = null;
        }

        TriggerDebouncedSave();
    }

    // ── DropNodeCommand ───────────────────────────────────────────────────

    private void ExecuteDropNode(NodeDropPayload? payload)
    {
        if (payload is null || payload.NodeType is null) return;

        BaseNode logical;
        var typeName = payload.NodeType.Name;

        switch (typeName)
        {
            case nameof(DelayNode):
                logical = new DelayNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_DelayNode_Name") ?? "Задержка" };
                break;
            case nameof(OverlayTextNode):
                logical = new OverlayTextNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_OverlayNode_Name") ?? "Оверлей текста" };
                break;
            case nameof(Win_SpeakTextNode):
                logical = new Win_SpeakTextNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_WinSpeakTextNode_Name") ?? "Озвучить текст" };
                break;
            case nameof(HotkeyTriggerNode):
                logical = new HotkeyTriggerNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_HotkeyNode_Name") ?? "Триггер клавиши" };
                break;
            case nameof(SpeechTriggerNode):
                logical = new SpeechTriggerNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_SpeechTriggerNode_Name") ?? "Триггер голоса" };
                break;
            case nameof(Web_RequestNode):
                logical = new Web_RequestNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_WebRequestNode_Name") ?? "Веб-запрос" };
                break;
            case nameof(SendInputNode):
                logical = new SendInputNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_SendInputNode_Name") ?? "Эмуляция клавиш" };
                break;
            case nameof(MouseActionNode):
                logical = new MouseActionNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_MouseActionNode_Name") ?? "Управление мышью" };
                break;
            case nameof(RunProcessNode):
                logical = new RunProcessNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_RunProcessNode_Name") ?? "Запуск приложения" };
                break;
            case nameof(ClipboardNode):
                logical = new ClipboardNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_ClipboardNode_Name") ?? "Буфер обмена" };
                break;
            case nameof(TextConditionNode):
                logical = new TextConditionNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_TextConditionNode_Name") ?? "Текстовое условие" };
                break;
            case nameof(OBS_SceneManagerNode):
                logical = new OBS_SceneManagerNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_ObsSceneManagerNode_Name") ?? "OBS: Сцены" };
                break;
            case nameof(OBS_SourceVisibilityManagerNode):
                logical = new OBS_SourceVisibilityManagerNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_ObsSourceVisibilityManagerNode_Name") ?? "OBS: Источники и Фильтры" };
                break;
            case nameof(OBS_AudioManagerNode):
                logical = new OBS_AudioManagerNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_ObsAudioManagerNode_Name") ?? "OBS: Аудио" };
                break;
            case nameof(OBS_StreamAndRecordManagerNode):
                logical = new OBS_StreamAndRecordManagerNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_ObsStreamAndRecordManagerNode_Name") ?? "OBS: Стрим и Запись" };
                break;
            case nameof(OBS_DynamicContentManagerNode):
                logical = new OBS_DynamicContentManagerNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_ObsDynamicContentManagerNode_Name") ?? "OBS: Динамический контент" };
                break;
            case nameof(Win_ProcessManagerNode):
                logical = new Win_ProcessManagerNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_WinProcessManagerNode_Name") ?? "Управление процессами" };
                break;
            case nameof(Win_WindowManagerNode):
                logical = new Win_WindowManagerNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_WinWindowManagerNode_Name") ?? "Управление окнами" };
                break;
            case nameof(Win_SystemPowerNode):
                logical = new Win_SystemPowerNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_WinSystemPowerNode_Name") ?? "Питание и Мониторы" };
                break;
            case nameof(Win_PowerShellNode):
                logical = new Win_PowerShellNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_WinPowerShellNode_Name") ?? "PowerShell / CMD" };
                break;
            case nameof(Win_AudioDeviceNode):
                logical = new Win_AudioDeviceNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_WinAudioDeviceNode_Name") ?? "Аудиоустройство" };
                break;
            case nameof(Wait_SmartDelayNode):
                logical = new Wait_SmartDelayNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_WaitSmartDelayNode_Name") ?? "Умное ожидание" };
                break;
            case nameof(Logic_CounterNode):
                logical = new Logic_CounterNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_LogicCounterNode_Name") ?? "Счётчик" };
                break;
            case nameof(Logic_BranchNode):
                logical = new Logic_BranchNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_LogicBranchNode_Name") ?? "Ветвление условий" };
                break;
            case nameof(Vision_OcrNode):
                logical = new Vision_OcrNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_VisionOcrNode_Name") ?? "OCR: Распознавание" };
                break;
            case nameof(Win_TranslateNode):
                logical = new Win_TranslateNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_WinTranslateNode_Name") ?? "Перевод текста" };
                break;
            case nameof(Logic_SequenceNode):
                logical = new Logic_SequenceNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_LogicSequenceNode_Name") ?? "Очередь действий" };
                break;
            case nameof(Logic_QueueBlockNode):
                logical = new Logic_QueueBlockNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_LogicQueueBlockNode_Name") ?? "Блок Очереди" };
                break;
            case nameof(Win_BypassQueueNode):
                logical = new Win_BypassQueueNode
                    { Name = Strings.ResourceManager.GetString("Toolbox_BypassQueueNode_Name") ?? "Вне очереди" };
                break;
            case nameof(TriggerRootNode):
                // Только одна TriggerRootNode на граф — если уже есть, игнорируем дроп
                if (VisualNodes.Any(vn => vn.LogicalNode is TriggerRootNode))
                {
                    _ = _logger.LogInfoAsync(nameof(BlueprintEditorViewModel),
                        "TriggerRootNode уже присутствует на графе. Дроп отклонён.");
                    return;
                }
                logical = new TriggerRootNode { Name = "▶ Старт" };
                break;
            default:
                _ = _logger.LogErrorAsync(nameof(BlueprintEditorViewModel),
                    $"Попытка создания неизвестного типа ноды: {typeName}");
                return;
        }

        var vn = AddVisualNode(logical, payload.X, payload.Y);
        SelectedNode = vn;
        TriggerDebouncedSave();
    }

    // ── Toolbox шаблоны ───────────────────────────────────────────────────

    private static IReadOnlyList<NodeTemplateViewModel> BuildNodeTemplates() =>
    [
        // ── 1. СИСТЕМА И ВВОД ─────────────────────────────────────────────
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(DelayNode),
            "Toolbox_DelayNode_Name",
            "Toolbox_DelayNode_Desc",
            // Иконка: циферблат с часовой стрелкой
            "M8,0C3.6,0 0,3.6 0,8 0,12.4 3.6,16 8,16 12.4,16 16,12.4 16,8 16,3.6 12.4,0 8,0Z " +
            "M8,14C4.7,14 2,11.3 2,8 2,4.7 4.7,2 8,2 11.3,2 14,4.7 14,8 14,11.3 11.3,14 8,14Z " +
            "M8.5,4L7.5,4 7.5,9 12,11.6 12.5,10.8 8.5,8.5Z",
            NodeCategory.SystemAndInput
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(HotkeyTriggerNode),
            "Toolbox_HotkeyNode_Name",
            "Toolbox_HotkeyNode_Desc",
            // Иконка: клавиатура с клавишами (ожидание ввода)
            "M15,3L1,3C0.4,3 0,3.4 0,4L0,12C0,12.6 0.4,13 1,13L15,13C15.6,13 16,12.6 16,12L16,4C16,3.4 15.6,3 15,3Z " +
            "M4,7L2,7 2,6 4,6Z M4,9L2,9 2,8 4,8Z M4,11L2,11 2,10 4,10Z " +
            "M7,7L5,7 5,6 7,6Z M7,9L5,9 5,8 7,8Z M7,11L5,11 5,10 7,10Z " +
            "M10,7L8,7 8,6 10,6Z M10,9L8,9 8,8 10,8Z M10,11L8,11 8,10 10,10Z " +
            "M14,7L11,7 11,6 14,6Z M13,9L11,9 11,8 13,8Z M14,11L11,11 11,10 14,10Z",
            NodeCategory.SystemAndInput
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(SpeechTriggerNode),
            "Toolbox_SpeechTriggerNode_Name",
            "Toolbox_SpeechTriggerNode_Desc",
            // Иконка: микрофон (голосовой ввод)
            "M8,0C6.3,0 5,1.3 5,3L5,8C5,9.7 6.3,11 8,11 9.7,11 11,9.7 11,8L11,3C11,1.3 9.7,0 8,0Z " +
            "M3,7C3,10.3 5.4,13 8,13 10.6,13 13,10.3 13,7L12,7C12,9.8 10.2,12 8,12 5.8,12 4,9.8 4,7Z " +
            "M7,14L7,16 6,16 6,16.5 10,16.5 10,16 9,16 9,14Z",
            NodeCategory.SystemAndInput
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(SendInputNode),
            "Toolbox_SendInputNode_Name",
            "Toolbox_SendInputNode_Desc",
            // Иконка: компактная клавиатура (слева) + стрелка вправо → (отправка в ОС)
            "M10,3L1,3C0.4,3 0,3.4 0,4L0,12C0,12.6 0.4,13 1,13L10,13C10.6,13 11,12.6 11,12L11,4C11,3.4 10.6,3 10,3Z " +
            "M3,6L2,6 2,5 3,5Z M6,6L5,6 5,5 6,5Z M9,6L8,6 8,5 9,5Z " +
            "M3,8L2,8 2,7 3,7Z M6,8L5,8 5,7 6,7Z M9,8L8,8 8,7 9,7Z " +
            "M9,11L2,11 2,10 9,10Z " +
            "M12,5L16,8 12,11Z",
            NodeCategory.SystemAndInput
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(MouseActionNode),
            "Toolbox_MouseActionNode_Name",
            "Toolbox_MouseActionNode_Desc",
            // Иконка: курсор мыши (стрелка)
            "M1,1L1,12 4,9 6,14 8,13 6,8 10,8Z",
            NodeCategory.SystemAndInput
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(RunProcessNode),
            "Toolbox_RunProcessNode_Name",
            "Toolbox_RunProcessNode_Desc",
            // Иконка: прямоугольник с выходящей стрелкой (внешняя ссылка / запуск)
            "M9,1L15,1 15,7 13,7 13,3 9,3Z " +
            "M6,0L8,0 8,2 2,2 2,14 14,14 14,8 16,8 16,16 0,16 0,0Z " +
            "M8,6L14,0 16,0 16,2 10,8Z",
            NodeCategory.SystemAndInput
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(ClipboardNode),
            "Toolbox_ClipboardNode_Name",
            "Toolbox_ClipboardNode_Desc",
            // Иконка: планшет-буфер обмена (прямоугольник с вкладкой сверху)
            "M11,1L11,0 5,0 5,1 3,1 3,14 13,14 13,1Z " +
            "M11,3L5,3 5,2 11,2Z " +
            "M5,6L11,6 11,7 5,7Z M5,9L11,9 11,10 5,10Z M5,12L9,12 9,13 5,13Z",
            NodeCategory.SystemAndInput
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(TextConditionNode),
            "Toolbox_TextConditionNode_Name",
            "Toolbox_TextConditionNode_Desc",
            // Иконка: ромб условия с вертикальной осью (развилка)
            "M8,0L15,8 8,16 1,8Z",
            NodeCategory.SystemAndInput
        )),

        // ── 2. СИСТЕМА (Windows автоматизация) ───────────────────────────
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(Win_ProcessManagerNode),
            "Toolbox_WinProcessManagerNode_Name",
            "Toolbox_WinProcessManagerNode_Desc",
            // Иконка: шестерня (системный процесс)
            "M8,5C6.3,5 5,6.3 5,8 5,9.7 6.3,11 8,11 9.7,11 11,9.7 11,8 11,6.3 9.7,5 8,5ZM14.4,9C14.5,8.7 14.5,8.3 14.5,8 14.5,7.7 14.5,7.3 14.4,7L16,5.8 14.5,3.2 12.6,3.9C12.1,3.5 11.6,3.2 11,3L10.7,1 5.3,1 5,3C4.4,3.2 3.9,3.5 3.4,3.9L1.5,3.2 0,5.8 1.6,7C1.5,7.3 1.5,7.7 1.5,8 1.5,8.3 1.5,8.7 1.6,9L0,10.2 1.5,12.8 3.4,12.1C3.9,12.5 4.4,12.8 5,13L5.3,15 10.7,15 11,13C11.6,12.8 12.1,12.5 12.6,12.1L14.5,12.8 16,10.2Z",
            NodeCategory.WindowsSystem
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(Win_WindowManagerNode),
            "Toolbox_WinWindowManagerNode_Name",
            "Toolbox_WinWindowManagerNode_Desc",
            // Иконка: окно с заголовком (три кнопки TitleBar)
            "M0,1L16,1 16,15 0,15Z M0,1L16,1 16,5 0,5Z M2,3A1,1 0 1 0 4,3A1,1 0 0 0 2,3Z M5.5,3A1,1 0 1 0 7.5,3A1,1 0 0 0 5.5,3Z",
            NodeCategory.WindowsSystem
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(Win_SystemPowerNode),
            "Toolbox_WinSystemPowerNode_Name",
            "Toolbox_WinSystemPowerNode_Desc",
            // Иконка: кнопка питания (вертикальная черта + дуга)
            "M7,0L9,0 9,8 7,8Z M4,2.5C1.6,4 0,6.3 0,9 0,12.9 3.6,16 8,16 12.4,16 16,12.9 16,9 16,6.3 14.4,4 12,2.5L11,4.3C12.8,5.5 14,7.1 14,9 14,11.8 11.3,14 8,14 4.7,14 2,11.8 2,9 2,7.1 3.2,5.5 5,4.3Z",
            NodeCategory.WindowsSystem
        )),

        // ── 3. СКРИПТЫ И ДАННЫЕ ──────────────────────────────────────────
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(Win_PowerShellNode),
            "Toolbox_WinPowerShellNode_Name",
            "Toolbox_WinPowerShellNode_Desc",
            // Иконка: терминал (рамка + > и _ )
            "M0,2L16,2 16,14 0,14Z M2,4L6,7 2,10Z M7,9L14,9 14,11 7,11Z",
            NodeCategory.ScriptsAndData
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(Win_AudioDeviceNode),
            "Toolbox_WinAudioDeviceNode_Name",
            "Toolbox_WinAudioDeviceNode_Desc",
            // Иконка: наушники
            "M8,1C3.6,1 0,4.3 0,8.5L0,12C0,13.1 0.9,14 2,14 3.1,14 4,13.1 4,12L4,10C4,8.9 3.1,8 2,8L2,8.5C2,5.5 4.7,3 8,3 11.3,3 14,5.5 14,8.5L14,8C12.9,8 12,8.9 12,10L12,12C12,13.1 12.9,14 14,14 15.1,14 16,13.1 16,12L16,8.5C16,4.3 12.4,1 8,1Z",
            NodeCategory.ScriptsAndData
        )),

        // ── 4. УМНАЯ ЛОГИКА ───────────────────────────────────────────────
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(Wait_SmartDelayNode),
            "Toolbox_WaitSmartDelayNode_Name",
            "Toolbox_WaitSmartDelayNode_Desc",
            // Иконка: глаз + лупа
            "M8,2C4,2 1,8 1,8 1,8 4,14 8,14 12,14 15,8 15,8 15,8 12,2 8,2Z M8,5A3,3 0 1 1 8,11A3,3 0 0 1 8,5Z M13,13L16,16 14.5,16 11.5,13Z",
            NodeCategory.SmartLogic
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(Logic_CounterNode),
            "Toolbox_LogicCounterNode_Name",
            "Toolbox_LogicCounterNode_Desc",
            // Иконка: счётчик (прямоугольник + цифра + стрелка вверх)
            "M0,3L10,3 10,13 0,13Z M2,5L8,5 8,7 2,7Z M2,9L6,9 6,11 2,11Z M12,4L12,12 14,12 14,4Z M11,3L15,3 13,1Z",
            NodeCategory.SmartLogic
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(Logic_BranchNode),
            "Toolbox_LogicBranchNode_Name",
            "Toolbox_LogicBranchNode_Desc",
            // Иконка: развилка (ромб с двумя стрелками вниз)
            "M8,0L15,7 8,14 1,7Z M8,14L5,16 M8,14L11,16 M5,16L5,18 M11,16L11,18",
            NodeCategory.SmartLogic
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(Logic_QueueBlockNode),
            "Toolbox_LogicQueueBlockNode_Name",
            "Toolbox_LogicQueueBlockNode_Desc",
            // Иконка: шлюз (ромб + стрелка вправо через барьер)
            "M6,8L0,4 0,12Z M15,4H9V6H13V10H9V12H15V4Z M9,7H11V9H9Z",
            NodeCategory.SmartLogic
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(Win_BypassQueueNode),
            "Toolbox_BypassQueueNode_Name",
            "Toolbox_BypassQueueNode_Desc",
            // Иконка: молния (VIP-обход, мгновенный запуск)
            "M13,1V8H19L11,23V16H5L13,1Z",
            NodeCategory.SmartLogic
        )),

        // ── 5. ИНТЕГРАЦИЯ И СЕТЬ ─────────────────────────────────────────
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(Web_RequestNode),
            "Toolbox_WebRequestNode_Name",
            "Toolbox_WebRequestNode_Desc",
            // Иконка: глобус (сфера с горизонтальными и вертикальными дугами)
            "M8,0C3.6,0 0,3.6 0,8 0,12.4 3.6,16 8,16 12.4,16 16,12.4 16,8 16,3.6 12.4,0 8,0Z " +
            "M8,1.5C9.4,3.2 10.2,5.5 10.3,8L5.7,8C5.8,5.5 6.6,3.2 8,1.5Z " +
            "M8,14.5C6.6,12.8 5.8,10.5 5.7,8L10.3,8C10.2,10.5 9.4,12.8 8,14.5Z " +
            "M1.7,5L5.3,5C4.9,6 4.7,7 4.7,8L1.3,8C1.4,6.9 1.5,5.9 1.7,5Z " +
            "M14.3,5C14.5,5.9 14.6,6.9 14.7,8L11.3,8C11.3,7 11.1,6 10.7,5Z",
            NodeCategory.NetworkAndApi
        )),

        // ── 5. ЗРЕНИЕ И ИИ ────────────────────────────────────────────────
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(Vision_OcrNode),
            "Toolbox_VisionOcrNode_Name",
            "Toolbox_VisionOcrNode_Desc",
            // Иконка: глаз с зрачком (OCR)
            "M8,3C4.5,3 1.5,5.2 0,8 1.5,10.8 4.5,13 8,13 11.5,13 14.5,10.8 16,8 14.5,5.2 11.5,3 8,3Z " +
            "M8,5.5C9.4,5.5 10.5,6.6 10.5,8 10.5,9.4 9.4,10.5 8,10.5 6.6,10.5 5.5,9.4 5.5,8 5.5,6.6 6.6,5.5 8,5.5Z " +
            "M8,6.8C7.3,6.8 6.8,7.3 6.8,8 6.8,8.7 7.3,9.2 8,9.2 8.7,9.2 9.2,8.7 9.2,8 9.2,7.3 8.7,6.8 8,6.8Z",
            NodeCategory.VisionAndAi
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(Win_TranslateNode),
            "Toolbox_WinTranslateNode_Name",
            "Toolbox_WinTranslateNode_Desc",
            // Иконка: двойная стрелка перевода (A ↔ B)
            "M0,4L6,4 6,2 10,5 6,8 6,6 0,6Z " +
            "M16,12L10,12 10,14 6,11 10,8 10,10 16,10Z",
            NodeCategory.VisionAndAi
        )),

        // ── 6. ИНТЕРФЕЙС И HUD ───────────────────────────────────────────
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(OverlayTextNode),
            "Toolbox_OverlayNode_Name",
            "Toolbox_OverlayNode_Desc",
            // Иконка: монитор с подставкой
            "M14,1L2,1C1.4,1 1,1.4 1,2L1,11C1,11.6 1.4,12 2,12L6,12 5.5,14 3,14 3,15 13,15 13,14 10.5,14 10,12 14,12C14.6,12 15,11.6 15,11L15,2C15,1.4 14.6,1 14,1Z " +
            "M14,11L2,11 2,2 14,2Z",
            NodeCategory.UserInterface
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(Win_SpeakTextNode),
            "Toolbox_WinSpeakTextNode_Name",
            "Toolbox_WinSpeakTextNode_Desc",
            // Иконка: речевой пузырь с динамиком
            "M1,1L11,1 11,8 1,8Z M4,8L4,11 7,8Z M13,2L13,4C14.7,4.5 16,5.6 16,7 16,8.4 14.7,9.5 13,10L13,12C15.8,11.4 18,9.4 18,7 18,4.6 15.8,2.6 13,2Z",
            NodeCategory.UserInterface
        )),

        // ── 6. OBS Studio (5 менеджеров) ─────────────────────────────────
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(OBS_SceneManagerNode),
            "Toolbox_ObsSceneManagerNode_Name",
            "Toolbox_ObsSceneManagerNode_Desc",
            // Иконка: три горизонтальных слоя со стрелкой переключения
            "M0,2L16,2 16,5 0,5Z M0,7L16,7 16,10 0,10Z M0,12L10,12 10,15 0,15Z M12,12L16,12 16,15 12,15Z M13,9L16,13 10,13Z",
            NodeCategory.OBS
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(OBS_SourceVisibilityManagerNode),
            "Toolbox_ObsSourceVisibilityManagerNode_Name",
            "Toolbox_ObsSourceVisibilityManagerNode_Desc",
            // Иконка: глаз (видимость источников)
            "M8,3C4,3 1,8 1,8 1,8 4,13 8,13 12,13 15,8 15,8 15,8 12,3 8,3Z M8,5.5A2.5,2.5 0 1 1 8,10.5A2.5,2.5 0 0 1 8,5.5Z",
            NodeCategory.OBS
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(OBS_AudioManagerNode),
            "Toolbox_ObsAudioManagerNode_Name",
            "Toolbox_ObsAudioManagerNode_Desc",
            // Иконка: динамик с волнами громкости
            "M2,5L6,5 10,1 10,15 6,11 2,11Z M12,5C13.7,6 14.7,7 14.7,8 14.7,9 13.7,10 12,11Z M14,3C16.5,5 16.5,11 14,13Z",
            NodeCategory.OBS
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(OBS_StreamAndRecordManagerNode),
            "Toolbox_ObsStreamAndRecordManagerNode_Name",
            "Toolbox_ObsStreamAndRecordManagerNode_Desc",
            // Иконка: кружок записи + волна вещания
            "M5,5A3,3 0 1 1 5,11A3,3 0 0 1 5,5Z M10,6C12,6.5 13.5,7.5 13.5,8 13.5,8.5 12,9.5 10,10Z M10,3C14,4 16,6 16,8 16,10 14,12 10,13Z",
            NodeCategory.OBS
        )),
        new NodeTemplateViewModel(new NodeTemplate(
            typeof(OBS_DynamicContentManagerNode),
            "Toolbox_ObsDynamicContentManagerNode_Name",
            "Toolbox_ObsDynamicContentManagerNode_Desc",
            // Иконка: монитор с курсором-карандашом (редактирование контента)
            "M1,1L12,1 12,10 1,10Z M2,2L11,2 11,9 2,9Z M13,3L15,5 9,11 7,11 7,9Z M14,1L16,3 15,4 13,2Z",
            NodeCategory.OBS
        )),
    ];

    private async Task OnCaptureCoordinatesAsync(MouseActionNode node)
    {
        var mainWindow = System.Windows.Application.Current?.MainWindow;
        var prevState  = mainWindow?.WindowState ?? System.Windows.WindowState.Normal;
        if (mainWindow is not null)
            mainWindow.WindowState = System.Windows.WindowState.Minimized;

        await Task.Delay(200); // ждём завершения анимации сворачивания

        var picker = new ARK.UI.Views.CoordinatePickerWindow();
        if (picker.ShowDialog() == true)
        {
            node.X = picker.ResultX;
            node.Y = picker.ResultY;
        }

        if (mainWindow is not null)
            mainWindow.WindowState = prevState;
    }

    private async Task OnTestNodeAsync()
    {
        var vn = SelectedNode;
        if (vn is null) return;
        var logicalNode = vn.LogicalNode;

        IsDebugConsoleOpen = true;
        NodeDebugLogs = string.Empty;
        AppendDebugLog($"▶ Тест ноды: «{logicalNode.Name}» [{logicalNode.GetType().Name}]");

        await _logger.LogInfoAsync(nameof(BlueprintEditorViewModel),
            $"[КОНСТРУКТОР] Запущен интерактивный тест ноды '{logicalNode.Name}'...")
            .ConfigureAwait(false);

        var testCtx = new MacroExecutionContext();
        testCtx.Variables["IsInteractiveTest"] = true;
        logicalNode.DebugSink = AppendDebugLog;
        bool success = await Task.Run(async () =>
            await logicalNode.ExecuteAsync(_serviceProvider, _logger, testCtx, CancellationToken.None)
                .ConfigureAwait(false)
        ).ConfigureAwait(false);
        logicalNode.DebugSink = null;

        await _logger.LogInfoAsync(nameof(BlueprintEditorViewModel),
            $"[КОНСТРУКТОР] Тест ноды '{logicalNode.Name}' завершён: {(success ? "Success" : "Failed")}.")
            .ConfigureAwait(false);

        AppendDebugLog(success ? "✓ Статус: Успех" : "✗ Статус: Ошибка");
        if (logicalNode.LastOutputValue is not null)
            AppendDebugLog(
                $"[ВЫХОД] Нода '{logicalNode.Name}' выдала в порт: \"{logicalNode.LastOutputValue}\" " +
                $"(тип: {logicalNode.LastOutputValue.GetType().Name})");
    }

    private async Task OnTestChainAsync()
    {
        var vn = SelectedNode;
        if (vn is null || _currentMacro is null) return;
        var logicalNode = vn.LogicalNode;

        IsDebugConsoleOpen = true;
        NodeDebugLogs = string.Empty;
        _nodeEngine.RegisterNodes(_currentMacro.VisualNodes.Select(n => n.LogicalNode));
        _nodeEngine.RegisterConnections(_currentMacro.VisualConnections);
        _nodeEngine.DebugSink = AppendDebugLog;

        await Task.Run(async () =>
        {
            await _logger.LogInfoAsync(nameof(BlueprintEditorViewModel),
                $"[КОНСТРУКТОР] Запущен интерактивный тест цепочки начиная с ноды '{logicalNode.Name}'...")
                .ConfigureAwait(false);
            var testCtx = new MacroExecutionContext();
            testCtx.Variables["IsInteractiveTest"] = true;
            await _nodeEngine.StartAsync(logicalNode.Id, testCtx, CancellationToken.None)
                .ConfigureAwait(false);
            await _logger.LogInfoAsync(nameof(BlueprintEditorViewModel),
                "[КОНСТРУКТОР] Тест цепочки успешно завершён.")
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

        _nodeEngine.DebugSink = null;
    }

    private async Task RefreshActiveProcessesAsync()
    {
        var infos = await Task.Run(() =>
        {
            var processes = Process.GetProcesses();
            try
            {
                return processes
                    .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle))
                    .Select(p =>
                    {
                        string  name = p.ProcessName + ".exe";
                        string? path = null;
                        try { path = p.MainModule?.FileName; } catch { }
                        return (name, path);
                    })
                    .DistinctBy(x => x.name)
                    .OrderBy(x => x.name)
                    .Select(x =>
                    {
                        var icon = ProcessIconHelper.GetIcon(x.path)
                                   ?? ProcessIconHelper.FallbackIcon;
                        return new ProcessInfo(x.name, x.path ?? string.Empty, icon);
                    })
                    .ToList();
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }).ConfigureAwait(false);

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            ActiveSystemProcesses.Clear();
            ActiveSystemProcesses.Add(new ProcessInfo(Win_WindowManagerNode.AllWindowsKey, string.Empty, null));
            foreach (var info in infos)
                ActiveSystemProcesses.Add(info);
        });
    }

    private void RefreshAvailableVoices()
    {
        AvailableVoices.Clear();
        switch (_configService.Current.SelectedTtsMode)
        {
            case TtsMode.Standard:
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "TTS", "Piper");
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, "*.onnx").OrderBy(x => x))
                    AvailableVoices.Add(Path.GetFileNameWithoutExtension(f));
                break;
            }
            case TtsMode.Kokoro:
            {
                var kokoroOnnx = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "Models", "TTS", "Kokoro", "kokoro-v1.0.onnx");
                if (!File.Exists(kokoroOnnx)) break;
                try
                {
                    if (KokoroVoiceManager.Voices.Count == 0)
                        KokoroVoiceManager.LoadVoicesFromPath();
                    foreach (var v in KokoroVoiceManager.Voices.OrderBy(v => v.Name))
                        AvailableVoices.Add(v.Name);
                }
                catch { }
                break;
            }
            case TtsMode.Disabled:
                break;
        }
    }

    // ── Подписка на PropertyChanged нод ──────────────────────────────────

    private void SubscribeNode(VisualNode vn)
    {
        vn.LogicalNode.PropertyChanged += OnNodePropertyChanged;
        vn.PropertyChanged             += OnVisualNodePositionChanged;
    }

    private void UnsubscribeNode(VisualNode vn)
    {
        vn.LogicalNode.PropertyChanged -= OnNodePropertyChanged;
        vn.PropertyChanged             -= OnVisualNodePositionChanged;
    }

    // Перетаскивание ноды — дебаунс 400мс, чтобы не флудить сохранениями во время drag.
    private void OnVisualNodePositionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(VisualNode.X) or nameof(VisualNode.Y))) return;
        ScheduleDebouncedSave();
    }

    // Явный вызов из Code-Behind после завершения drag — гарантирует сохранение позиции.
    public void TriggerDebouncedSave() => ScheduleDebouncedSave();

    private void ScheduleDebouncedSave()
    {
        if (_currentProfile is null) return;

        // Cancel без Dispose: Task.Delay держит ссылку на токен отменённого CTS.
        // Немедленный Dispose бросает ObjectDisposedException в таймере ThreadPool.
        // CTS без unmanaged-ресурсов — GC подберёт после завершения Task.
        _debounceCts?.Cancel();
        var cts = _debounceCts = new CancellationTokenSource();
        _ = Task.Delay(400, cts.Token).ContinueWith(
            t => { if (!t.IsCanceled) _ = SaveCurrentAsync(); },
            TaskScheduler.Default);
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BaseNode.State) && sender is BaseNode logicalNode)
        {
            var newState = logicalNode.State;
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var vn = VisualNodes.FirstOrDefault(n => ReferenceEquals(n.LogicalNode, logicalNode));
                if (vn is null) return;
                vn.State = newState;
                TriggerConnectionAnimations(vn, newState);
            });
            return;
        }

        // Каскад: SelectedScene изменилась → перезагружаем AvailableSources
        if (e.PropertyName == nameof(IObsCascadeNode.SelectedScene)
            && sender is IObsCascadeNode cascadeScene
            && ReferenceEquals(sender, _selectedNode?.LogicalNode))
        {
            _ = RefreshAvailableSourcesAsync(cascadeScene.SelectedScene);
            return;
        }

        // Каскад: SelectedSource изменилась → перезагружаем AvailableFilters (только для SourceVisibility)
        if (e.PropertyName == nameof(IObsCascadeNode.SelectedSource)
            && sender is OBS_SourceVisibilityManagerNode visNode
            && ReferenceEquals(sender, _selectedNode?.LogicalNode))
        {
            _ = RefreshAvailableFiltersAsync(visNode.SelectedSource);
            return;
        }

        SaveCurrent();
    }

    private void TriggerConnectionAnimations(VisualNode node, NodeState state)
    {
        if (state is not (NodeState.Success or NodeState.Failed)) return;
        var isErrorRoute = state == NodeState.Failed;
        foreach (var line in ConnectionLines.Where(l =>
            l.Source.NodeId == node.NodeId && !l.IsDataRoute && l.IsErrorRoute == isErrorRoute))
            _ = PulseConnectionLineAsync(line);
        if (state == NodeState.Success)
            foreach (var line in ConnectionLines.Where(l =>
                l.Source.NodeId == node.NodeId && l.IsDataRoute))
                _ = PulseConnectionLineAsync(line);
    }

    private static async Task PulseConnectionLineAsync(VisualConnectionLine line)
    {
        line.IsActive = true;
        await Task.Delay(1500).ConfigureAwait(false);
        var app = System.Windows.Application.Current;
        if (app is null) return;
        await app.Dispatcher.InvokeAsync(() => line.IsActive = false);
    }

    public void ResetAllNodeStates()
    {
        foreach (var vn   in VisualNodes)     vn.State    = NodeState.Pending;
        foreach (var line in ConnectionLines) line.IsActive = false;
    }

    // ── Сохранение ────────────────────────────────────────────────────────

    private void SaveCurrent()
    {
        if (_currentProfile is null) return;
        // Пересобираем индекс ключевых слов перед сохранением — граф мог измениться
        _currentMacro?.RebuildVoiceKeywordsIndex();
        _ = SaveCurrentAsync();
    }

    private async Task SaveCurrentAsync()
    {
        try
        {
            await _profileService.SaveProfileAsync(_currentProfile!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync("BlueprintEditor", "Ошибка автосохранения Blueprint.", ex)
                .ConfigureAwait(false);
        }
    }
}
