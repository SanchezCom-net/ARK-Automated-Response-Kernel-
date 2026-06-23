using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Nodes;
using ARK.UI.Core.Services;
using ARK.UI.Resources;

namespace ARK.UI.ViewModels;

public sealed class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly INodeEngine            _nodeEngine;
    private readonly IConfigService         _configService;
    private readonly INetworkService        _networkService;
    private readonly ILogService            _logger;
    private readonly IStartupOrchestrator   _orchestrator;

    private string    _networkStatusText;
    private bool      _isNetworkConnected;
    private string    _logSearchQuery    = string.Empty;
    private bool      _isOverlayChecked;
    private int       _selectedTabIndex;
    private string    _webSocketUrl      = "ws://localhost:8080";
    private BaseNode? _selectedNode;
    private bool      _isReady;

    public ObservableCollection<BaseNode> RegisteredNodes { get; } = new();

    // Построчный терминал логов: каждая строка подсвечивается LogHighlightConverter
    public ObservableCollection<string> LogLines { get; } = new();

    // Плоский текст всего лога для слоя TextBox (выделение + копирование).
    // Обновляется единым вызовом в ReplaceLogLines — одна нотификация вместо 150.
    private string _logOutputText = string.Empty;
    public string LogOutputText
    {
        get => _logOutputText;
        private set => SetProperty(ref _logOutputText, value);
    }

    public string NetworkStatusText
    {
        get => _networkStatusText;
        private set => SetProperty(ref _networkStatusText, value);
    }

    public bool IsNetworkConnected
    {
        get => _isNetworkConnected;
        private set => SetProperty(ref _isNetworkConnected, value);
    }

    // Поисковый запрос терминала логов; UI шлёт изменения с Delay=150 — без лагов при вводе
    public string LogSearchQuery
    {
        get => _logSearchQuery;
        set => SetProperty(ref _logSearchQuery, value);
    }

    /// <summary>
    /// Становится true когда StartupOrchestrator завершил все фазы прогрева.
    /// Пока false — поверх UI отображается спиннер инициализации.
    /// </summary>
    public bool IsReady
    {
        get => _isReady;
        private set => SetProperty(ref _isReady, value);
    }

    public bool IsOverlayChecked
    {
        get => _isOverlayChecked;
        set
        {
            if (!SetProperty(ref _isOverlayChecked, value)) return;
            _configService.Current.IsOverlayEnabled = value;
            _ = _configService.SaveAsync();
        }
    }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public string WebSocketUrl
    {
        get => _webSocketUrl;
        set => SetProperty(ref _webSocketUrl, value);
    }

    public BaseNode? SelectedNode
    {
        get => _selectedNode;
        set => SetProperty(ref _selectedNode, value);
    }

    public ICommand ShowExplorerCommand      { get; }
    public ICommand ShowAutomationCommand    { get; }
    public ICommand ShowQueueCommand         { get; }
    public ICommand ShowAiSettingsCommand    { get; }
    public ICommand ShowLogsCommand          { get; }
    public ICommand ShowObsCommand           { get; }
    public ICommand ShowTwitchCommand        { get; }
    public ICommand StartScenarioCommand     { get; }
    public ICommand ReconnectNetworkCommand  { get; }
    public ICommand RefreshLogsCommand       { get; }

    public BlueprintEditorViewModel  BlueprintEditor { get; }
    public MacroExplorerViewModel    MacroExplorer   { get; }
    public QueueViewModel            QueueSettings   { get; }
    public SettingsViewModel         Settings        { get; }
    public ObsSettingsViewModel      ObsSettings     { get; }
    public TwitchSettingsViewModel   TwitchSettings  { get; }

    public DashboardViewModel(
        INodeEngine              nodeEngine,
        IConfigService           configService,
        INetworkService          networkService,
        ILogService              logger,
        IStartupOrchestrator     startupOrchestrator,
        BlueprintEditorViewModel blueprintEditor,
        MacroExplorerViewModel   macroExplorer,
        QueueViewModel           queueViewModel,
        SettingsViewModel        settingsViewModel,
        ObsSettingsViewModel     obsSettingsViewModel,
        TwitchSettingsViewModel  twitchSettingsViewModel)
    {
        _nodeEngine      = nodeEngine;
        _configService   = configService;
        _networkService  = networkService;
        _logger          = logger;
        _orchestrator    = startupOrchestrator;
        BlueprintEditor  = blueprintEditor;
        MacroExplorer    = macroExplorer;
        QueueSettings    = queueViewModel;
        Settings         = settingsViewModel;
        ObsSettings      = obsSettingsViewModel;
        TwitchSettings   = twitchSettingsViewModel;

        // Если оркестратор уже завершил прогрев (окно открылось позже) — сразу готовы.
        _isReady = startupOrchestrator.IsReady;
        if (!_isReady)
            startupOrchestrator.ReadyStateChanged += OnOrchestratorReady;

        _isOverlayChecked   = configService.Current.IsOverlayEnabled;
        _isNetworkConnected = networkService.IsConnected;
        _networkStatusText  = _isNetworkConnected ? Strings.Net_Connected : Strings.Net_Disconnected;

        foreach (var node in nodeEngine.Nodes)
            RegisteredNodes.Add(node);

        networkService.ConnectionStatusChanged += OnConnectionStatusChanged;
        LocalizationService.CultureChanged     += OnCultureChanged;

        // При открытии макроса из проводника — загружаем документ в Blueprint и переключаем вкладку
        MacroExplorer.MacroOpenRequested += doc =>
        {
            BlueprintEditor.LoadFromMacro(doc);
            SelectedTabIndex = 1;
        };

        // Tab 0 = Проводник, Tab 1 = Blueprint, Tab 2 = Очередь, Tab 3 = ИИ и Сеть,
        // Tab 4 = Логи, Tab 5 = OBS, Tab 6 = Twitch
        ShowExplorerCommand   = new RelayCommand(_ => SelectedTabIndex = 0);
        ShowAutomationCommand = new RelayCommand(_ => SelectedTabIndex = 1);
        ShowQueueCommand      = new RelayCommand(_ => SelectedTabIndex = 2);
        ShowAiSettingsCommand = new RelayCommand(_ =>
        {
            SelectedTabIndex = 3;
            _ = Settings.LoadOllamaModelsAsync();
        });
        ShowLogsCommand       = new RelayCommand(_ => SelectedTabIndex = 4);
        ShowObsCommand        = new RelayCommand(_ => SelectedTabIndex = 5);
        ShowTwitchCommand     = new RelayCommand(_ => SelectedTabIndex = 6);

        StartScenarioCommand = new AsyncRelayCommand(
            async _ =>
            {
                if (_selectedNode is null) return;
                await _nodeEngine.StartAsync(_selectedNode.Id);
            },
            _ => _selectedNode is not null && !_nodeEngine.IsRunning);

        ReconnectNetworkCommand = new AsyncRelayCommand(
            async _ =>
            {
                if (!Uri.TryCreate(_webSocketUrl, UriKind.Absolute, out var uri)) return;
                await _networkService.ConnectAsync(uri);
            });

        RefreshLogsCommand = new AsyncRelayCommand(async _ => await LoadLogsAsync());
    }

    private void OnOrchestratorReady(object? sender, EventArgs e)
    {
        _orchestrator.ReadyStateChanged -= OnOrchestratorReady;
        // Переключаем на UI-поток: IsReady стреляет PropertyChanged → WPF-биндинг
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => IsReady = true);
    }

    private void OnConnectionStatusChanged(object? sender, bool connected)
    {
        IsNetworkConnected = connected;
        NetworkStatusText  = connected ? Strings.Net_Connected : Strings.Net_Disconnected;
    }

    private void OnCultureChanged(object? sender, EventArgs e)
        => NetworkStatusText = _isNetworkConnected ? Strings.Net_Connected : Strings.Net_Disconnected;

    private async Task LoadLogsAsync()
    {
        try
        {
            var logFile = Path.Combine(_logger.LogDirectory,
                $"log_{DateTime.Now:yyyy-MM-dd}.json");
            if (!File.Exists(logFile))
            {
                ReplaceLogLines([Strings.Logs_NoEntries]);
                return;
            }
            var lines = await File.ReadAllLinesAsync(logFile);
            ReplaceLogLines(lines.TakeLast(150));
        }
        catch (Exception ex)
        {
            ReplaceLogLines([string.Format(Strings.Logs_ReadError, ex.Message)]);
        }
    }

    // AsyncRelayCommand выполняется в UI-контексте — ObservableCollection меняется безопасно
    private void ReplaceLogLines(IEnumerable<string> lines)
    {
        LogLines.Clear();
        foreach (var line in lines)
            LogLines.Add(line);
        // LogOutputText обновляется один раз после полного заполнения LogLines:
        // TextBox получает одну PropertyChanged-нотификацию, а не 150 CollectionChanged.
        LogOutputText = string.Join('\n', LogLines);
    }

    public void Dispose()
    {
        _orchestrator.ReadyStateChanged         -= OnOrchestratorReady;
        _networkService.ConnectionStatusChanged -= OnConnectionStatusChanged;
        LocalizationService.CultureChanged      -= OnCultureChanged;
        BlueprintEditor.Dispose();
        Settings.Dispose();
        ObsSettings.Dispose();
        TwitchSettings.Dispose();
        // MacroExplorer не имеет ресурсов для освобождения; подписка через лямбду очистится с GC
    }
}
