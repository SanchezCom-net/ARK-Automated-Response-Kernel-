using ARK.UI.Core.Interfaces;
using Newtonsoft.Json.Linq;
using SysAction = System.Action;

namespace ARK.UI.Core.Services;

public sealed partial class ObsService : IObsService
{
    // ── ШАГ 2: Каскадные запросы ────────────────────────────────────────────

    public async Task<List<string>> GetSceneInputsAsync(string sceneName, CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected || string.IsNullOrEmpty(sceneName)) return [];
        try
        {
            var items = await Task.Run(() => _obs.GetSceneItemList(sceneName), cancellationToken).ConfigureAwait(false);
            return items?.Select(i => i.SourceName).Where(n => !string.IsNullOrEmpty(n)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService), $"[OBS] Ошибка GetSceneItemList '{sceneName}': {ex.Message}").ConfigureAwait(false);
            return [];
        }
    }

    public async Task<List<string>> GetSourceFiltersAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected || string.IsNullOrEmpty(sourceName)) return [];
        try
        {
            var filters = await Task.Run(() => _obs.GetSourceFilterList(sourceName), cancellationToken).ConfigureAwait(false);
            return filters?.Select(f => f.Name).Where(n => !string.IsNullOrEmpty(n)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService), $"[OBS] Ошибка GetSourceFilterList '{sourceName}': {ex.Message}").ConfigureAwait(false);
            return [];
        }
    }

    public async Task<List<string>> GetAudioSourcesAsync(CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected) return [];
        try
        {
            var inputs = await Task.Run(() => _obs.GetInputList(""), cancellationToken).ConfigureAwait(false);
            return inputs?.Select(i => i.InputName).Where(n => !string.IsNullOrEmpty(n)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService), $"[OBS] Ошибка GetInputList: {ex.Message}").ConfigureAwait(false);
            return [];
        }
    }

    // ── Видимость источников ─────────────────────────────────────────────────

    public async Task<int> GetSceneItemIdAsync(string sceneName, string sourceName, CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected) return -1;
        try
        {
            return await Task.Run(() => _obs.GetSceneItemId(sceneName, sourceName, 0), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService), $"[OBS] Ошибка GetSceneItemId '{sourceName}' в '{sceneName}': {ex.Message}").ConfigureAwait(false);
            return -1;
        }
    }

    public async Task<bool> GetSceneItemEnabledAsync(string sceneName, int sceneItemId, CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected) return false;
        try
        {
            return await Task.Run(() => _obs.GetSceneItemEnabled(sceneName, sceneItemId), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService), $"[OBS] Ошибка GetSceneItemEnabled id={sceneItemId}: {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }

    public Task SetSceneItemEnabledAsync(string sceneName, int sceneItemId, bool enabled, CancellationToken cancellationToken = default)
        => RunObsAsync(() => _obs.SetSceneItemEnabled(sceneName, sceneItemId, enabled),
            $"[OBS] Источник id={sceneItemId} в '{sceneName}' → {(enabled ? "виден" : "скрыт")}", cancellationToken);

    public async Task<bool> GetSourceFilterEnabledAsync(string sourceName, string filterName, CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected) return false;
        try
        {
            var filter = await Task.Run(() => _obs.GetSourceFilter(sourceName, filterName), cancellationToken).ConfigureAwait(false);
            return filter?.IsEnabled ?? false;
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService), $"[OBS] Ошибка GetSourceFilter '{filterName}': {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }

    public Task SetSourceFilterEnabledAsync(string sourceName, string filterName, bool enabled, CancellationToken cancellationToken = default)
        => RunObsAsync(() => _obs.SetSourceFilterEnabled(sourceName, filterName, enabled),
            $"[OBS] Фильтр '{filterName}' у '{sourceName}' → {(enabled ? "включён" : "выключен")}", cancellationToken);

    // ── Аудио ────────────────────────────────────────────────────────────────

    public async Task<float> GetInputVolumeDbAsync(string inputName, CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected) return -60f;
        try
        {
            var vol = await Task.Run(() => _obs.GetInputVolume(inputName), cancellationToken).ConfigureAwait(false);
            return vol.VolumeDb;
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService), $"[OBS] Ошибка GetInputVolume '{inputName}': {ex.Message}").ConfigureAwait(false);
            return -60f;
        }
    }

    public Task SetInputVolumeDbAsync(string inputName, float volumeDb, CancellationToken cancellationToken = default)
        => RunObsAsync(() => _obs.SetInputVolume(inputName, volumeDb, true),
            $"[OBS] Громкость '{inputName}' → {volumeDb:F1} дБ", cancellationToken);

    public Task ToggleInputMuteAsync(string inputName, CancellationToken cancellationToken = default)
        => RunObsAsync(() => _obs.ToggleInputMute(inputName),
            $"[OBS] Mute '{inputName}' переключён", cancellationToken);

    // ── Вещание ──────────────────────────────────────────────────────────────

    public async Task<bool> IsStreamingAsync(CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected) return false;
        try
        {
            return await Task.Run(() => _obs.GetStreamStatus().IsActive, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService), $"[OBS] Ошибка GetStreamStatus: {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }

    public Task StartStreamingAsync(CancellationToken cancellationToken = default)
        => RunObsAsync(_obs.StartStream, "[OBS] Трансляция запущена.", cancellationToken);

    public Task StopStreamingAsync(CancellationToken cancellationToken = default)
        => RunObsAsync(_obs.StopStream, "[OBS] Трансляция остановлена.", cancellationToken);

    public Task ToggleStreamingAsync(CancellationToken cancellationToken = default)
        => RunObsAsync(() => { _obs.ToggleStream(); }, "[OBS] Трансляция переключена.", cancellationToken);

    // ── Буфер повторов ───────────────────────────────────────────────────────

    public async Task<bool> IsReplayBufferActiveAsync(CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected) return false;
        try
        {
            return await Task.Run(() => _obs.GetReplayBufferStatus(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService), $"[OBS] Ошибка GetReplayBufferStatus: {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }

    public Task StartReplayBufferAsync(CancellationToken cancellationToken = default)
        => RunObsAsync(_obs.StartReplayBuffer, "[OBS] Буфер повторов запущен.", cancellationToken);

    public Task StopReplayBufferAsync(CancellationToken cancellationToken = default)
        => RunObsAsync(_obs.StopReplayBuffer, "[OBS] Буфер повторов остановлен.", cancellationToken);

    public Task ToggleReplayBufferAsync(CancellationToken cancellationToken = default)
        => RunObsAsync(() => { _obs.ToggleReplayBuffer(); }, "[OBS] Буфер повторов переключён.", cancellationToken);

    public Task SaveReplayBufferAsync(CancellationToken cancellationToken = default)
        => RunObsAsync(_obs.SaveReplayBuffer, "[OBS] Буфер повторов сохранён.", cancellationToken);

    // ── Динамический контент ─────────────────────────────────────────────────

    public async Task<string> GetInputTextAsync(string inputName, CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected) return string.Empty;
        try
        {
            var settings = await Task.Run(() => _obs.GetInputSettings(inputName), cancellationToken).ConfigureAwait(false);
            return settings?.Settings?["text"]?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService), $"[OBS] Ошибка GetInputText '{inputName}': {ex.Message}").ConfigureAwait(false);
            return string.Empty;
        }
    }

    public Task SetInputTextAsync(string inputName, string text, CancellationToken cancellationToken = default)
        => RunObsAsync(() => _obs.SetInputSettings(inputName, new JObject { ["text"] = text }, true),
            $"[OBS] Текст '{inputName}' обновлён.", cancellationToken);

    public async Task<string> GetInputFilePathAsync(string inputName, CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected) return string.Empty;
        try
        {
            var settings = await Task.Run(() => _obs.GetInputSettings(inputName), cancellationToken).ConfigureAwait(false);
            return settings?.Settings?["file"]?.ToString()
                ?? settings?.Settings?["local_file"]?.ToString()
                ?? string.Empty;
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService), $"[OBS] Ошибка GetInputFilePath '{inputName}': {ex.Message}").ConfigureAwait(false);
            return string.Empty;
        }
    }

    public Task SetInputFilePathAsync(string inputName, string filePath, CancellationToken cancellationToken = default)
        => RunObsAsync(() =>
            {
                var settings = _obs.GetInputSettings(inputName);
                bool isLocal = settings?.Settings?.ContainsKey("local_file") == true;
                string key = isLocal ? "local_file" : "file";
                _obs.SetInputSettings(inputName, new JObject { [key] = filePath }, true);
            },
            $"[OBS] Путь файла '{inputName}' обновлён.", cancellationToken);

    public Task TriggerMediaActionAsync(string inputName, string mediaAction, CancellationToken cancellationToken = default)
        => RunObsAsync(() => _obs.TriggerMediaInputAction(inputName, mediaAction),
            $"[OBS] Медиа '{inputName}' → {mediaAction}", cancellationToken);

    public async Task<string> GetMediaStateAsync(string inputName, CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected) return string.Empty;
        try
        {
            var status = await Task.Run(() => _obs.GetMediaInputStatus(inputName), cancellationToken).ConfigureAwait(false);
            return status?.StateString ?? string.Empty;
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService), $"[OBS] Ошибка GetMediaInputStatus '{inputName}': {ex.Message}").ConfigureAwait(false);
            return string.Empty;
        }
    }
}
