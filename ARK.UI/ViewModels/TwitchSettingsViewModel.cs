using System.Windows.Input;
using WpfApp = System.Windows.Application;
using ARK.UI.Core.Interfaces;
using ARK.UI.Resources;

namespace ARK.UI.ViewModels;

public sealed class TwitchSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly ITwitchService  _twitchService;
    private readonly IConfigService  _configService;
    private readonly ILogService     _logger;
    private readonly IVaultService   _vault;

    private string _twitchChannel  = string.Empty;
    private string _twitchUsername = string.Empty;
    private string _oauthToken     = string.Empty;
    private bool   _isConnected;
    private bool   _isTokenVisible;

    public string TwitchChannel
    {
        get => _twitchChannel;
        set => SetProperty(ref _twitchChannel, value);
    }

    public string TwitchUsername
    {
        get => _twitchUsername;
        set => SetProperty(ref _twitchUsername, value);
    }

    public string OAuthToken
    {
        get => _oauthToken;
        set => SetProperty(ref _oauthToken, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(TwitchStatusText));
        }
    }

    public bool IsTokenVisible
    {
        get => _isTokenVisible;
        set => SetProperty(ref _isTokenVisible, value);
    }

    public string TwitchStatusText => _isConnected
        ? Strings.Twitch_StatusConnected
        : Strings.Twitch_StatusDisconnected;

    public ICommand ConnectCommand               { get; }
    public ICommand DisconnectCommand            { get; }
    public ICommand SaveCommand                  { get; }
    public ICommand ToggleTokenVisibilityCommand { get; }

    public TwitchSettingsViewModel(
        ITwitchService  twitchService,
        IConfigService  configService,
        ILogService     logger,
        IVaultService   vault)
    {
        _twitchService = twitchService;
        _configService = configService;
        _logger        = logger;
        _vault         = vault;

        var cfg = configService.Current;
        _twitchChannel  = cfg.TwitchChannel;
        _twitchUsername = cfg.TwitchUsername;
        _isConnected    = twitchService.IsConnected;

        _ = LoadOAuthTokenAsync();

        twitchService.ConnectionStatusChanged += OnConnectionStatusChanged;

        ToggleTokenVisibilityCommand = new RelayCommand(_ => IsTokenVisible = !IsTokenVisible);

        ConnectCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                await _twitchService.ConnectAsync(
                    TwitchChannel, TwitchUsername, OAuthToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _logger.LogErrorAsync(nameof(TwitchSettingsViewModel),
                    $"[TWITCH] Ошибка подключения: {ex.Message}").ConfigureAwait(false);
            }
        }, _ => !_twitchService.IsConnected);

        DisconnectCommand = new AsyncRelayCommand(async _ =>
        {
            await _twitchService.DisconnectAsync().ConfigureAwait(false);
        }, _ => _twitchService.IsConnected);

        SaveCommand = new AsyncRelayCommand(async _ =>
        {
            var c = _configService.Current;
            c.TwitchChannel  = TwitchChannel.Trim();
            c.TwitchUsername = TwitchUsername.Trim();
            await _configService.UpdateTwitchOAuthAsync(OAuthToken.Trim()).ConfigureAwait(false);
            await _logger.LogInfoAsync(nameof(TwitchSettingsViewModel),
                "[TWITCH] Настройки Twitch сохранены.").ConfigureAwait(false);
        });
    }

    private async Task LoadOAuthTokenAsync()
    {
        if (string.IsNullOrEmpty(_configService.Current.EncryptedTwitchOAuth)) return;
        try
        {
            var token = await _vault.DecryptAsync(_configService.Current.EncryptedTwitchOAuth)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token))
                WpfApp.Current?.Dispatcher.Invoke(() => OAuthToken = token);
        }
        catch (Exception ex)
        {
            _ = _logger.LogWarningAsync(nameof(TwitchSettingsViewModel),
                $"[TWITCH] Не удалось дешифровать OAuth-токен из DPAPI: {ex.Message}");
        }
    }

    private void OnConnectionStatusChanged(object? sender, bool connected)
    {
        WpfApp.Current?.Dispatcher.Invoke(() =>
        {
            IsConnected = connected;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        });
    }

    public void Dispose()
    {
        _twitchService.ConnectionStatusChanged -= OnConnectionStatusChanged;
    }
}
