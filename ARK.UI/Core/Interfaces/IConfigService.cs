using ARK.UI.Core.Models;

namespace ARK.UI.Core.Interfaces;

public interface IConfigService
{
    /// <summary>Срабатывает после каждого успешного SaveAsync. Используется для авто-триггеров (ModelManager, etc.).</summary>
    event System.Action? ConfigSaved;

    /// <summary>Пользовательская конфигурация (config.json) — изменяется во время работы.</summary>
    AppConfig Current { get; }

    /// <summary>Конфигурация среды (appsettings.json) — только чтение, загружается при старте.</summary>
    AppSettings AppSettings { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);

    /// <summary>Шифрует rawApiKey через IVaultService и сохраняет в конфиг.</summary>
    Task UpdateApiKeyAsync(string rawApiKey, CancellationToken cancellationToken = default);

    /// <summary>Расшифровывает и возвращает API-ключ. Возвращает string.Empty если ключ не задан.</summary>
    Task<string> GetApiKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>Шифрует rawPassword через IVaultService и сохраняет как EncryptedObsPassword.</summary>
    Task UpdateObsPasswordAsync(string rawPassword, CancellationToken cancellationToken = default);

    /// <summary>Расшифровывает и возвращает пароль OBS WebSocket. Возвращает string.Empty если не задан.</summary>
    Task<string> GetObsPasswordAsync(CancellationToken cancellationToken = default);

    /// <summary>Шифрует rawToken через IVaultService и сохраняет как EncryptedTwitchOAuth.</summary>
    Task UpdateTwitchOAuthAsync(string rawToken, CancellationToken cancellationToken = default);

    /// <summary>Расшифровывает и возвращает OAuth-токен Twitch. Возвращает string.Empty если не задан.</summary>
    Task<string> GetTwitchOAuthAsync(CancellationToken cancellationToken = default);
}
