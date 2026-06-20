using System.Windows.Input;
using WpfApp = System.Windows.Application;
using ARK.UI.Core.Interfaces;
using ARK.UI.Resources;

namespace ARK.UI.ViewModels;

public sealed class ObsSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly IObsService    _obsService;
    private readonly IConfigService _configService;
    private readonly ILogService    _logger;
    private readonly IVaultService  _vault;

    private string _obsIpAddress      = "127.0.0.1";
    private string _obsPort           = "4455";
    private string _obsPassword       = string.Empty;
    private bool   _obsAutoConnect;
    private bool   _obsAutoReconnect;
    private bool   _isObsConnected;
    private bool   _isPasswordVisible;

    public string ObsIpAddress
    {
        get => _obsIpAddress;
        set => SetProperty(ref _obsIpAddress, value);
    }

    public string ObsPort
    {
        get => _obsPort;
        set => SetProperty(ref _obsPort, value);
    }

    public string ObsPassword
    {
        get => _obsPassword;
        set => SetProperty(ref _obsPassword, value);
    }

    public bool ObsAutoConnect
    {
        get => _obsAutoConnect;
        set => SetProperty(ref _obsAutoConnect, value);
    }

    public bool ObsAutoReconnect
    {
        get => _obsAutoReconnect;
        set => SetProperty(ref _obsAutoReconnect, value);
    }

    public bool IsObsConnected
    {
        get => _isObsConnected;
        private set
        {
            if (!SetProperty(ref _isObsConnected, value)) return;
            OnPropertyChanged(nameof(ObsStatusText));
        }
    }

    public bool IsPasswordVisible
    {
        get => _isPasswordVisible;
        set => SetProperty(ref _isPasswordVisible, value);
    }

    public string ObsStatusText => _isObsConnected
        ? Strings.Obs_StatusConnected
        : Strings.Obs_StatusDisconnected;

    public ICommand ConnectObsCommand              { get; }
    public ICommand DisconnectObsCommand           { get; }
    public ICommand SaveObsSettingsCommand         { get; }
    public ICommand TogglePasswordVisibilityCommand { get; }

    public ObsSettingsViewModel(
        IObsService    obsService,
        IConfigService configService,
        ILogService    logger,
        IVaultService  vault)
    {
        _obsService    = obsService;
        _configService = configService;
        _logger        = logger;
        _vault         = vault;

        var cfg = configService.Current;

        // Разбираем сохранённый URL (ws://ip:port) на два поля
        if (Uri.TryCreate(cfg.ObsWebSocketUrl, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
        {
            _obsIpAddress = uri.Host;
            _obsPort      = uri.Port > 0 ? uri.Port.ToString() : "4455";
        }

        _obsAutoConnect    = cfg.ObsAutoConnect;
        _obsAutoReconnect  = cfg.ObsAutoReconnect;
        _isObsConnected    = obsService.IsConnected;

        TogglePasswordVisibilityCommand = new AsyncRelayCommand(_ =>
        {
            IsPasswordVisible = !IsPasswordVisible;
            return Task.CompletedTask;
        });

        // Загружаем сохранённый пароль из DPAPI асинхронно (fire-and-forget — ок для отображения)
        _ = LoadPasswordAsync();

        obsService.ConnectionStatusChanged += OnConnectionStatusChanged;

        ConnectObsCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                // Принудительно расшифровываем пароль через IVaultService если поле пустое.
                // Это исключает race condition: LoadPasswordAsync мог не завершиться к моменту нажатия кнопки.
                var pwd = ObsPassword;
                if (string.IsNullOrEmpty(pwd)
                    && !string.IsNullOrEmpty(_configService.Current.EncryptedObsPassword))
                {
                    pwd = await _vault.DecryptAsync(_configService.Current.EncryptedObsPassword)
                        .ConfigureAwait(false);
                    WpfApp.Current?.Dispatcher.Invoke(() => ObsPassword = pwd);
                }

                // Очищаем IP от случайно введённых пользователем префиксов (ws://, http://, порты)
                var fullUrl = BuildObsUrl();
                var safePwd = pwd ?? string.Empty;
                await _logger.LogInfoAsync(nameof(ObsSettingsViewModel),
                    $"[OBS] ConnectObsCommand → URL: {fullUrl} | Длина пароля: {safePwd.Length} | Пустой: {safePwd.Length == 0}")
                    .ConfigureAwait(false);
                await _obsService.ConnectAsync(fullUrl, safePwd).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _logger.LogErrorAsync(nameof(ObsSettingsViewModel),
                    $"[OBS] Ошибка подключения: {ex.Message}").ConfigureAwait(false);
            }
        }, _ => !_obsService.IsConnected);

        DisconnectObsCommand = new AsyncRelayCommand(async _ =>
        {
            await _obsService.DisconnectAsync().ConfigureAwait(false);
        }, _ => _obsService.IsConnected);

        SaveObsSettingsCommand = new AsyncRelayCommand(async _ =>
        {
            cfg.ObsWebSocketUrl         = BuildObsUrl();
            cfg.ObsAutoConnect          = ObsAutoConnect;
            cfg.ObsAutoReconnect        = ObsAutoReconnect;
            await _configService.UpdateObsPasswordAsync(ObsPassword).ConfigureAwait(false);
            await _configService.SaveAsync().ConfigureAwait(false);
            await _logger.LogInfoAsync(nameof(ObsSettingsViewModel),
                "[OBS] Настройки сохранены.").ConfigureAwait(false);
        });

    }

    // Строит корректный URI ws://ip:port, очищая IP от случайных префиксов и портов в строке.
    private string BuildObsUrl()
    {
        var ip   = SanitizeIp(ObsIpAddress);
        var port = ObsPort.Trim().Split('/', '?')[0].Trim();
        return $"ws://{ip}:{port}";
    }

    private static string SanitizeIp(string input) =>
        input
            .Replace("ws://",   "", StringComparison.OrdinalIgnoreCase)
            .Replace("wss://",  "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("https://","", StringComparison.OrdinalIgnoreCase)
            .Split(':')[0]
            .Trim();

    private async Task LoadPasswordAsync()
    {
        if (string.IsNullOrEmpty(_configService.Current.EncryptedObsPassword)) return;
        try
        {
            var pwd = await _vault.DecryptAsync(_configService.Current.EncryptedObsPassword)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(pwd))
                WpfApp.Current?.Dispatcher.Invoke(() => ObsPassword = pwd);
        }
        catch (Exception ex)
        {
            _ = _logger.LogWarningAsync(nameof(ObsSettingsViewModel),
                $"[OBS] Не удалось дешифровать пароль из хранилища DPAPI: {ex.Message}");
        }
    }

    private void OnConnectionStatusChanged(object? sender, bool connected)
    {
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            IsObsConnected = connected;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        });
    }

    public void Dispose()
    {
        _obsService.ConnectionStatusChanged -= OnConnectionStatusChanged;
    }
}
