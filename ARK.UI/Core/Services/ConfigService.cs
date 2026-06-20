using System.IO;
using System.Text.Json;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

public sealed class ConfigService : IConfigService
{
    public event System.Action? ConfigSaved;

    private readonly IVaultService  _vault;
    private readonly ILogService    _logger;
    private readonly SemaphoreSlim  _semaphore        = new(1, 1);
    private readonly string         _configPath;
    private readonly string         _appSettingsPath;

    public AppConfig    Current     { get; private set; } = new();
    public AppSettings  AppSettings { get; private set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // PropertyNameCaseInsensitive: пользователь может писать "version" или "Version"
    private static readonly JsonSerializerOptions AppSettingsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ConfigService(IVaultService vault, ILogService logger)
    {
        _vault           = vault;
        _logger          = logger;
        _configPath      = Path.Combine(AppContext.BaseDirectory, "config.json");
        _appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAppSettingsInternalAsync(cancellationToken).ConfigureAwait(false);

            if (!File.Exists(_configPath))
            {
                await _logger.LogInfoAsync(nameof(ConfigService),
                    "config.json не найден — применяется конфигурация по умолчанию.");
                await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var json   = await File.ReadAllTextAsync(_configPath, cancellationToken).ConfigureAwait(false);
            var config = JsonSerializer.Deserialize<AppConfig>(json)
                         ?? throw new JsonException("Десериализация вернула null.");

            Current = config;
            await _logger.LogInfoAsync(nameof(ConfigService), "Конфигурация загружена успешно.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _logger.LogWarningAsync(nameof(ConfigService),
                $"Ошибка чтения config.json ({ex.Message}) — Fallback: конфигурация по умолчанию.");
            Current = new AppConfig();
            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // Вызывается строго под _semaphore (внутри LoadAsync)
    private async Task LoadAppSettingsInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_appSettingsPath))
        {
            AppSettings = new AppSettings();
            NormalizeVoskSettings(AppSettings.VoskSettings); // авто-определяет ModelBasePath
            NormalizeTriggerSettings(AppSettings.Trigger);
            await SaveAppSettingsInternalAsync(cancellationToken).ConfigureAwait(false);
            await _logger.LogInfoAsync(nameof(ConfigService),
                "appsettings.json не найден — создан с значениями по умолчанию.")
                .ConfigureAwait(false);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_appSettingsPath, cancellationToken)
                .ConfigureAwait(false);
            AppSettings = JsonSerializer.Deserialize<AppSettings>(json, AppSettingsJsonOptions)
                          ?? new AppSettings();

            bool repaired = NormalizeVoskSettings(AppSettings.VoskSettings)
                          | NormalizeTriggerSettings(AppSettings.Trigger);
            if (repaired)
            {
                await SaveAppSettingsInternalAsync(cancellationToken).ConfigureAwait(false);
                await _logger.LogWarningAsync(nameof(ConfigService),
                    "[ConfigService] appsettings.json содержал неполные VoskSettings — " +
                    $"восстановлен. ModelBasePath='{AppSettings.VoskSettings.ModelBasePath}'.")
                    .ConfigureAwait(false);
            }
            else
            {
                await _logger.LogInfoAsync(nameof(ConfigService),
                    $"appsettings.json загружен (v{AppSettings.AppInfo.Version}). " +
                    $"ModelBasePath='{AppSettings.VoskSettings.ModelBasePath}'.")
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _logger.LogWarningAsync(nameof(ConfigService),
                $"Ошибка чтения appsettings.json ({ex.Message}) — Fallback: значения по умолчанию.")
                .ConfigureAwait(false);
            AppSettings = new AppSettings();
            NormalizeVoskSettings(AppSettings.VoskSettings);
            NormalizeTriggerSettings(AppSettings.Trigger);
            try { await SaveAppSettingsInternalAsync(cancellationToken).ConfigureAwait(false); }
            catch { /* best effort */ }
        }
    }

    // Нормализует VoskSettings: заполняет нулевые int и пустой ModelBasePath значениями по умолчанию.
    // Возвращает true если были правки (нужно записать файл обратно).
    private static bool NormalizeVoskSettings(VoskSettingsSection s)
    {
        var d = new VoskSettingsSection(); // property initializers = канонические дефолты
        bool dirty = false;

        if (s.StartupTimeoutMs <= 0) { s.StartupTimeoutMs = d.StartupTimeoutMs; dirty = true; }
        if (s.RestartLimit     <= 0) { s.RestartLimit     = d.RestartLimit;     dirty = true; }
        if (s.IdleTimeoutMs    <= 0) { s.IdleTimeoutMs    = d.IdleTimeoutMs;    dirty = true; }
        if (s.MaxMemoryMb      <= 0) { s.MaxMemoryMb      = d.MaxMemoryMb;      dirty = true; }
        if (s.MaxSessionTimeMs <= 0) { s.MaxSessionTimeMs = d.MaxSessionTimeMs; dirty = true; }

        if (string.IsNullOrWhiteSpace(s.ModelBasePath))
        {
            s.ModelBasePath = Path.Combine(AppContext.BaseDirectory, "Models", "Vosk");
            dirty = true;
        }

        if (string.IsNullOrWhiteSpace(s.EngineType))
        {
            s.EngineType = d.EngineType;
            dirty = true;
        }

        if (string.IsNullOrWhiteSpace(s.HostProcessName))
        {
            s.HostProcessName = d.HostProcessName;
            dirty = true;
        }

        return dirty;
    }

    // Нормализует TriggerSettings: заполняет нулевой таймаут и пустой список ключевых слов.
    private static bool NormalizeTriggerSettings(TriggerSettings s)
    {
        var d = new TriggerSettings();
        bool dirty = false;

        if (s.ActivationTimeoutMs <= 0)
        {
            s.ActivationTimeoutMs = d.ActivationTimeoutMs;
            dirty = true;
        }

        if (s.ActivationKeywords is null || s.ActivationKeywords.Length == 0)
        {
            s.ActivationKeywords = d.ActivationKeywords;
            dirty = true;
        }

        return dirty;
    }

    // Сохраняет полный AppSettings в appsettings.json. Вызывается только под _semaphore.
    private async Task SaveAppSettingsInternalAsync(CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(AppSettings, JsonOptions);
        await File.WriteAllTextAsync(_appSettingsPath, json, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
        ConfigSaved?.Invoke(); // Уведомляем подписчиков (ModelManager, etc.) после сохранения
    }

    public async Task UpdateApiKeyAsync(string rawApiKey, CancellationToken cancellationToken = default)
    {
        Current.EncryptedApiKey = await _vault.EncryptAsync(rawApiKey, cancellationToken)
            .ConfigureAwait(false);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await _logger.LogInfoAsync(nameof(ConfigService), "API-ключ обновлён и зашифрован через DPAPI.");
    }

    public async Task<string> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(Current.EncryptedApiKey))
            return string.Empty;

        return await _vault.DecryptAsync(Current.EncryptedApiKey, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task UpdateObsPasswordAsync(string rawPassword, CancellationToken cancellationToken = default)
    {
        Current.EncryptedObsPassword = await _vault.EncryptAsync(rawPassword, cancellationToken)
            .ConfigureAwait(false);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await _logger.LogInfoAsync(nameof(ConfigService), "Пароль OBS WebSocket обновлён и зашифрован через DPAPI.");
    }

    public async Task<string> GetObsPasswordAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(Current.EncryptedObsPassword))
            return string.Empty;

        return await _vault.DecryptAsync(Current.EncryptedObsPassword, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task UpdateTwitchOAuthAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(rawToken))
        {
            Current.EncryptedTwitchOAuth = string.Empty;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        Current.EncryptedTwitchOAuth = await _vault.EncryptAsync(rawToken, cancellationToken)
            .ConfigureAwait(false);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await _logger.LogInfoAsync(nameof(ConfigService), "OAuth-токен Twitch обновлён и зашифрован через DPAPI.");
    }

    public async Task<string> GetTwitchOAuthAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(Current.EncryptedTwitchOAuth))
            return string.Empty;

        return await _vault.DecryptAsync(Current.EncryptedTwitchOAuth, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SaveInternalAsync(CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        await File.WriteAllTextAsync(_configPath, json, cancellationToken).ConfigureAwait(false);
        await _logger.LogInfoAsync(nameof(ConfigService), "Конфигурация сохранена на диск.");
    }
}
