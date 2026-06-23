using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Input;
using ARK.UI.Core.Action;
using ARK.UI.Core.Bus;
using ARK.UI.Core.Input;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Network;
using ARK.UI.Core.Nodes;
using ARK.UI.Core.Services;
using ARK.UI.Core.Vision;
using ARK.UI.Resources;
using ARK.UI.ViewModels;
using ARK.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Forms    = System.Windows.Forms;
using WpfApp   = System.Windows.Application;
using WpfMsgBox = System.Windows.MessageBox;

namespace ARK.UI;

public partial class App : WpfApp
{
    private IServiceProvider?       _serviceProvider;
    private ILogService?            _logger;
    private IOverlayService?        _overlayService;
    private IConfigService?         _configService;
    private IInputService?          _inputService;
    private INetworkService?        _networkService;
    private IWindowTrackerService?  _windowTrackerService;
    private ISpeechTriggerService?  _speechTriggerService;
    private IOllamaBridgeService?   _ollamaBridgeService;
    private ContextMenu?           _trayContextMenu;
    private Window?                _menuHelper;
    private Forms.NotifyIcon?      _trayIcon;

    // Ссылки на пункты меню для динамических обновлений
    internal MenuItem? MiStatus;
    internal MenuItem? MiRunPause;
    internal MenuItem? MiNetwork;
    internal MenuItem? MiLanguage;
    internal MenuItem? MiAiEnabled;
    internal bool      IsPaused;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException               += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException      += OnUnobservedTaskException;

        var services = new ServiceCollection();
        services.AddSingleton<IDataBus,        DataBus>();
        services.AddSingleton<IBlackBoxLogger, BlackBoxLogger>();
        services.AddSingleton<ILogService,    JsonLogService>();
        services.AddSingleton<IOverlayService, OverlayService>();
        services.AddSingleton<IVaultService,   VaultService>();
        services.AddSingleton<IConfigService,  ConfigService>();
        services.AddTransient<INodeEngine,    NodeEngine>();
        services.AddSingleton<IInputService,   InputService>();
        services.AddSingleton<IActionService,  ActionService>();
        services.AddSingleton<IVisionService,  VisionService>();
        services.AddSingleton<INetworkService,        NetworkService>();
        services.AddSingleton<IWindowTrackerService,  WindowTrackerService>();
        services.AddSingleton<IStorageManager,        StorageManager>();
        services.AddSingleton<IQueueManager,          QueueManager>();
        services.AddSingleton<IHardwareAccelerator,       HardwareAcceleratorService>();
        services.AddSingleton<IModelManager,              ModelManager>();
        services.AddSingleton<ITriggerService,            TriggerService>();
        services.AddSingleton<ISpeechTriggerService,    SpeechTriggerService>();
        services.AddSingleton<IOllamaBridgeService,     OllamaBridgeService>();
        services.AddSingleton<ISpeechSynthesisService,  SpeechSynthesisService>();
        services.AddSingleton<IUiAutomationService,     UiAutomationService>();
        services.AddSingleton<IObsService,              ObsService>();
        services.AddSingleton<ITwitchService,           TwitchService>();
        services.AddSingleton<IProcessWatcher,          ProcessWatcher>();
        services.AddSingleton<IActiveDocumentRegistry,  ActiveDocumentRegistry>();
        services.AddSingleton<IStartupOrchestrator,     StartupOrchestrator>();
        services.AddSingleton<IMacroOrchestrator,        MacroOrchestrator>();
        services.AddSingleton<IEventMonitor,             EventMonitor>();
        services.AddSingleton<NetworkCommandDispatcher>();
        services.AddTransient<BlueprintEditorViewModel>();
        services.AddTransient<MacroExplorerViewModel>();
        services.AddTransient<QueueViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ObsSettingsViewModel>();
        services.AddTransient<TwitchSettingsViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<DashboardWindow>();
        services.AddTransient<DiagnosticsService>();
        _serviceProvider = services.BuildServiceProvider();

        _logger               = _serviceProvider.GetRequiredService<ILogService>();
        _overlayService       = _serviceProvider.GetRequiredService<IOverlayService>();
        _configService        = _serviceProvider.GetRequiredService<IConfigService>();
        _inputService         = _serviceProvider.GetRequiredService<IInputService>();
        _networkService       = _serviceProvider.GetRequiredService<INetworkService>();
        _windowTrackerService = _serviceProvider.GetRequiredService<IWindowTrackerService>();
        _speechTriggerService = _serviceProvider.GetRequiredService<ISpeechTriggerService>();
        _ollamaBridgeService  = _serviceProvider.GetRequiredService<IOllamaBridgeService>();

        try
        {
            await _configService.LoadAsync();

            // Применяем сохранённый язык до инициализации меню
            if (_configService.Current.Language == "en")
            {
                var culture = new CultureInfo("en");
                Strings.Culture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
            }

            await _serviceProvider.GetRequiredService<IStorageManager>().EnsureDirectoriesAsync();
            await _logger.LogInfoAsync(nameof(App), Strings.App_Starting);
        }
        catch (Exception ex)
        {
            _ = _logger?.LogErrorAsync(nameof(App),
                "Ошибка инициализации при загрузке конфигурации.", ex);
        }

        InitializeTrayIcon();

        try
        {
            await _inputService.InitializeAsync();
            await _inputService.StartGlobalHooksAsync();
        }
        catch (Exception ex)
        {
            _ = _logger?.LogErrorAsync(nameof(App), "Ошибка запуска Input Module.", ex);
        }

        var nodeEngine = _serviceProvider.GetRequiredService<INodeEngine>();

        var node1 = new OverlayTextNode
        {
            Name                 = "Показ приветствия",
            Text                 = "AUTOMATED RESPONSE KERNEL ACTIVE",
            DurationMilliseconds = 3000
        };
        var node2 = new DelayNode
        {
            Name              = "Задержка",
            DelayMilliseconds = 1000
        };

        node1.OnSuccessNodeId = node2.Id;
        nodeEngine.RegisterNodes([node1, node2]);

        _ = nodeEngine.StartAsync(node1.Id);

        // Демо: Ctrl+Shift+A → оверлей напрямую через InputService (не через MacroScheduler).
        // Профиль НЕ добавляется в Profiles — исключает его отображение в UI и накопление на диске.
        var demoNode1 = node1;
        _inputService.KeyDown += (_, e) =>
        {
            if (e.Key == Key.A && e.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                _ = nodeEngine.StartAsync(demoNode1.Id);
        };

        // WindowTracker: отслеживание активного окна
        try
        {
            await _windowTrackerService!.StartAsync();
            await _logger!.LogInfoAsync(nameof(App), "WindowTrackerService запущен.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = _logger?.LogErrorAsync(nameof(App), "Ошибка запуска WindowTrackerService.", ex);
        }

        // Запуск локального JSON-RPC Command Center (порт 8888)
        _ = Task.Run(async () =>
        {
            try
            {
                await _networkService!.StartListeningAsync(8888, default).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ = _logger?.LogErrorAsync(nameof(App),
                    "[NETWORK] Ошибка запуска JSON-RPC Command Center.", ex);
            }
        });

        // Фоновое подключение с URL из конфигурации
        try
        {
            var wsUrl = _configService!.Current.WebSocketUrl;
            if (!string.IsNullOrWhiteSpace(wsUrl) && Uri.TryCreate(wsUrl, UriKind.Absolute, out var wsUri))
            {
                await _networkService.ConnectAsync(wsUri).ConfigureAwait(false);
                await _logger!.LogInfoAsync(nameof(App), $"Network Module запущен ({wsUrl}).").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _ = _logger?.LogErrorAsync(nameof(App), "Ошибка запуска Network Module.", ex);
        }

        // Авто-подключение OBS WebSocket (если включено в настройках)
        if (_configService!.Current.ObsAutoConnect)
        {
            var obsService = _serviceProvider!.GetRequiredService<IObsService>();
            var obsUrl     = _configService.Current.ObsWebSocketUrl;
            var obsPwd     = await _configService.GetObsPasswordAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(obsUrl))
            {
                // Task.Run + explicit try-catch: поглощает OperationCanceledException (таймаут),
                // InvalidOperationException (Disconnected при коннекте) и любые прочие ошибки.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await obsService.ConnectAsync(obsUrl, obsPwd).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await (_logger?.LogWarningAsync(nameof(App),
                            $"[OBS] Авто-подключение не удалось ({obsUrl}): {ex.Message}") ?? Task.CompletedTask)
                            .ConfigureAwait(false);
                    }
                });
            }
        }

        // StartupOrchestrator: прогрев всех подсистем (GPU → Speech → MacroIndex → Processes).
        // Выполняется строго в фоновом потоке — UI-поток не блокируется ни на миллисекунду.
        _ = Task.Run(() => _serviceProvider!.GetRequiredService<IStartupOrchestrator>().RunAsync());
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _ = _logger?.LogErrorAsync(nameof(Dispatcher), e.Exception.Message, e.Exception);
        WpfMsgBox.Show(Strings.Error_CriticalMessage, Strings.Error_CriticalTitle,
            MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        _ = _logger?.LogErrorAsync(nameof(AppDomain), exception?.Message ?? "Unknown", exception);
        try
        {
            Dispatcher.Invoke(() => WpfMsgBox.Show(Strings.Error_CriticalMessage,
                Strings.Error_CriticalTitle, MessageBoxButton.OK, MessageBoxImage.Error));
            Shutdown();
        }
        catch { Environment.Exit(1); }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // SetObserved() предотвращает аварийное завершение процесса .NET
        e.SetObserved();
        // Фоновые необработанные задачи (WebSocket, OBS, сеть) — не критичны: логируем, не крашимся
        _ = _logger?.LogErrorAsync(nameof(TaskScheduler),
            $"[BG] Поглощено необработанное исключение фоновой задачи: {e.Exception.InnerException?.Message ?? e.Exception.Message}",
            e.Exception);
    }

    private void InitializeTrayIcon()
    {
        _trayContextMenu = (ContextMenu)Resources["TrayContextMenu"];
        // При закрытии меню снимаем Topmost и прячем хелпер
        _trayContextMenu.Closed += (_, _) =>
        {
            if (_menuHelper is null) return;
            _menuHelper.Topmost = false;
            _menuHelper.Hide();
        };

        foreach (var obj in _trayContextMenu.Items)
        {
            if (obj is not MenuItem item) continue;
            switch (item.Tag?.ToString())
            {
                case "status":
                    item.Header = Strings.Tray_StatusActive;
                    MiStatus = item;
                    break;

                case "run_pause":
                    item.Header = Strings.Tray_RunPause;
                    item.Click += async (_, _) => await TogglePauseAsync();
                    MiRunPause = item;
                    break;

                case "overlay":
                    item.Header    = Strings.Tray_ToggleOverlay;
                    item.IsChecked = _configService?.Current.IsOverlayEnabled ?? true;
                    item.Click += async (_, _) => await ToggleOverlayAsync(item);
                    break;

                case "network":
                    item.Header = Strings.Tray_NetworkDisconnected;
                    MiNetwork = item;
                    break;

                case "dashboard":
                    item.Header = Strings.Tray_OpenDashboard;
                    item.Click += (_, _) => OpenDashboard();
                    break;

                case "restart":
                    item.Header = Strings.Tray_RestartApp;
                    item.Click += (_, _) => RestartApp();
                    break;

                case "language":
                    item.Header = Strings.Tray_ToggleLanguage;
                    item.Click += (_, _) => ToggleLanguage();
                    MiLanguage = item;
                    break;

                case "clear_logs":
                    item.Header = Strings.Tray_ClearLogs;
                    item.Click += async (_, _) => await ClearLogsAsync();
                    break;

                case "autostart":
                    item.Header    = Strings.Tray_AutoStart;
                    item.IsChecked = IsAutoStartEnabled();
                    item.Click += (_, _) => ToggleAutoStart(item);
                    break;

                case "reset_overlay":
                    item.Header = Strings.Tray_ResetOverlay;
                    item.Click += async (_, _) => await ResetOverlayAsync();
                    break;

                case "reset_ai_session":
                    item.Header = Strings.Tray_ResetAiSession;
                    item.Click += (_, _) =>
                    {
                        _ollamaBridgeService?.ResetSession();
                        _ = _logger?.LogInfoAsync(nameof(App),
                            "[ИИ] Сессия диалога сброшена из трея.");
                    };
                    break;

                case "ai_enabled":
                    item.Header    = Strings.Tray_AiEnabled;
                    item.IsChecked = _configService?.Current.IsAiEnabled ?? true;
                    item.Click    += async (_, _) =>
                    {
                        var cfg = _configService?.Current;
                        if (cfg is null) return;
                        cfg.IsAiEnabled  = !cfg.IsAiEnabled;
                        item.IsChecked   = cfg.IsAiEnabled;
                        if (_configService is not null)
                            await _configService.SaveAsync().ConfigureAwait(false);
                        _ = _logger?.LogInfoAsync(nameof(App),
                            cfg.IsAiEnabled
                                ? "[ИИ] ИИ-ассистент включён из трея."
                                : "[ИИ] ИИ-ассистент отключён из трея.");
                    };
                    MiAiEnabled = item;
                    break;

                case "exit":
                    item.Header = Strings.Tray_Exit;
                    item.Click += (_, _) => { _trayIcon?.Dispose(); Shutdown(); };
                    break;
            }
        }

        System.Drawing.Icon? arkIcon = null;
        try
        {
            var iconUri = new Uri("pack://application:,,,/Resources/app.ico");
            var rs = WpfApp.GetResourceStream(iconUri);
            if (rs is not null)
            {
                using var stream = rs.Stream;
                arkIcon = new System.Drawing.Icon(stream, new System.Drawing.Size(16, 16));
            }
        }
        catch { /* системная иконка как резерв */ }

        _trayIcon = new Forms.NotifyIcon
        {
            Icon    = arkIcon ?? System.Drawing.SystemIcons.Application,
            Text    = "ARK — Automated Response Kernel",
            Visible = true
        };
        _trayIcon.MouseClick += OnTrayIconMouseClick;

        if (_networkService is not null)
            _networkService.ConnectionStatusChanged += OnNetworkStatusChanged;
    }

    private void OnTrayIconMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Right || _trayContextMenu is null) return;

        EnsureMenuHelper();

        // Жёсткая Win32-цепочка активации: обходит блокировку Explorer.exe
        _menuHelper!.Topmost     = true;
        _menuHelper.WindowState  = WindowState.Normal;
        _menuHelper.Visibility   = Visibility.Visible;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(_menuHelper).Handle;
        Win32Api.SetActiveWindow(hwnd);
        Win32Api.BringWindowToTop(hwnd);
        Win32Api.SetForegroundWindow(hwnd);

        _trayContextMenu.PlacementTarget = _menuHelper;
        _trayContextMenu.Placement       = PlacementMode.MousePoint;
        _trayContextMenu.IsOpen          = true;
        _menuHelper.Activate();
    }

    private Window EnsureMenuHelper()
    {
        if (_menuHelper is not null) return _menuHelper;
        _menuHelper = new Window
        {
            Width              = 1,
            Height             = 1,
            Left               = -32000,
            Top                = -32000,
            ShowInTaskbar      = false,
            WindowStyle        = WindowStyle.None,
            AllowsTransparency = true,
            Background         = System.Windows.Media.Brushes.Transparent,
            ResizeMode         = ResizeMode.NoResize
        };
        _menuHelper.Show();
        return _menuHelper;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _windowTrackerService?.Stop();
            (_windowTrackerService as IDisposable)?.Dispose();
            (_networkService as IDisposable)?.Dispose();
            (_inputService   as IDisposable)?.Dispose();
            _logger?.LogInfoAsync(nameof(App), Strings.App_Exiting).GetAwaiter().GetResult();
        }
        catch { /* не блокируем завершение при ошибке логирования/хуков */ }

        _menuHelper?.Close();
        _trayIcon?.Dispose();

        // ServiceProvider реализует IAsyncDisposable — используем его в приоритете,
        // чтобы корректно освободить сервисы с IAsyncDisposable (напр. JsonLogService)
        if (_serviceProvider is IAsyncDisposable asyncDisposable)
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else if (_serviceProvider is IDisposable disposable)
            disposable.Dispose();

        base.OnExit(e);
    }

}
