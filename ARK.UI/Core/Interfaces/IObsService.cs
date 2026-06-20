namespace ARK.UI.Core.Interfaces;

public interface IObsService
{
    bool IsConnected { get; }

    /// <summary>Срабатывает при изменении статуса соединения: true = подключено, false = отключено.</summary>
    event EventHandler<bool> ConnectionStatusChanged;

    /// <summary>Подключение к obs-websocket 5.x (обычно ws://127.0.0.1:4455).</summary>
    Task ConnectAsync(string url, string password, CancellationToken cancellationToken = default);

    Task DisconnectAsync();

    /// <summary>Смена текущей программной сцены OBS.</summary>
    Task SetCurrentProgramSceneAsync(string sceneName, CancellationToken cancellationToken = default);

    /// <summary>Включение / выключение звука источника.</summary>
    Task SetInputMuteAsync(string inputName, bool mute, CancellationToken cancellationToken = default);

    Task StartRecordingAsync(CancellationToken cancellationToken = default);
    Task StopRecordingAsync(CancellationToken cancellationToken = default);
    Task ToggleRecordingAsync(CancellationToken cancellationToken = default);

    /// <summary>Возвращает имена всех сцен из текущего профиля OBS. Пустой список если не подключено.</summary>
    Task<List<string>> GetScenesAsync(CancellationToken cancellationToken = default);

    /// <summary>Возвращает имя текущей активной программной сцены. Пустая строка если не подключено.</summary>
    Task<string> GetCurrentSceneAsync(CancellationToken cancellationToken = default);

    /// <summary>Возвращает true если запись OBS активна. false если не подключено или запись остановлена.</summary>
    Task<bool> IsRecordingAsync(CancellationToken cancellationToken = default);

    /// <summary>Возвращает true если источник звука заглушён. false если не подключено или включён.</summary>
    Task<bool> IsInputMutedAsync(string inputName, CancellationToken cancellationToken = default);

    // ── ШАГ 2: Каскадные запросы ────────────────────────────────────────────

    /// <summary>Список имён всех источников в сцене (SceneItemList). Пустой список если не подключено.</summary>
    Task<List<string>> GetSceneInputsAsync(string sceneName, CancellationToken cancellationToken = default);

    /// <summary>Список имён фильтров источника. Пустой список если не подключено.</summary>
    Task<List<string>> GetSourceFiltersAsync(string sourceName, CancellationToken cancellationToken = default);

    /// <summary>Список имён всех аудиовходов OBS. Пустой список если не подключено.</summary>
    Task<List<string>> GetAudioSourcesAsync(CancellationToken cancellationToken = default);

    // ── Видимость источников (OBS_SourceVisibilityManagerNode) ─────────────

    Task<int> GetSceneItemIdAsync(string sceneName, string sourceName, CancellationToken cancellationToken = default);
    Task<bool> GetSceneItemEnabledAsync(string sceneName, int sceneItemId, CancellationToken cancellationToken = default);
    Task SetSceneItemEnabledAsync(string sceneName, int sceneItemId, bool enabled, CancellationToken cancellationToken = default);
    Task<bool> GetSourceFilterEnabledAsync(string sourceName, string filterName, CancellationToken cancellationToken = default);
    Task SetSourceFilterEnabledAsync(string sourceName, string filterName, bool enabled, CancellationToken cancellationToken = default);

    // ── Аудио (OBS_AudioManagerNode) ────────────────────────────────────────

    Task<float> GetInputVolumeDbAsync(string inputName, CancellationToken cancellationToken = default);
    Task SetInputVolumeDbAsync(string inputName, float volumeDb, CancellationToken cancellationToken = default);
    Task ToggleInputMuteAsync(string inputName, CancellationToken cancellationToken = default);

    // ── Вещание (OBS_StreamAndRecordManagerNode) ────────────────────────────

    Task<bool> IsStreamingAsync(CancellationToken cancellationToken = default);
    Task StartStreamingAsync(CancellationToken cancellationToken = default);
    Task StopStreamingAsync(CancellationToken cancellationToken = default);
    Task ToggleStreamingAsync(CancellationToken cancellationToken = default);

    // ── Буфер повторов (OBS_StreamAndRecordManagerNode) ─────────────────────

    Task<bool> IsReplayBufferActiveAsync(CancellationToken cancellationToken = default);
    Task StartReplayBufferAsync(CancellationToken cancellationToken = default);
    Task StopReplayBufferAsync(CancellationToken cancellationToken = default);
    Task ToggleReplayBufferAsync(CancellationToken cancellationToken = default);
    Task SaveReplayBufferAsync(CancellationToken cancellationToken = default);

    // ── Динамический контент (OBS_DynamicContentManagerNode) ────────────────

    Task<string> GetInputTextAsync(string inputName, CancellationToken cancellationToken = default);
    Task SetInputTextAsync(string inputName, string text, CancellationToken cancellationToken = default);
    Task<string> GetInputFilePathAsync(string inputName, CancellationToken cancellationToken = default);
    Task SetInputFilePathAsync(string inputName, string filePath, CancellationToken cancellationToken = default);
    Task TriggerMediaActionAsync(string inputName, string mediaAction, CancellationToken cancellationToken = default);
    Task<string> GetMediaStateAsync(string inputName, CancellationToken cancellationToken = default);
}
