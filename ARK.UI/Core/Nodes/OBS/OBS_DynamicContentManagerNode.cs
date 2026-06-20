using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes.OBS;

public sealed class OBS_DynamicContentManagerNode : BaseNode, IObsCascadeNode
{
    public override string DefaultDataInputPropertyName => nameof(TextContent);

    public static IReadOnlyList<ObsSceneMode>   AllModes        { get; } = [ObsSceneMode.Switch, ObsSceneMode.CheckActive];
    public static IReadOnlyList<ObsContentType> AllContentTypes { get; } = [ObsContentType.Text, ObsContentType.Image, ObsContentType.Media];
    public static IReadOnlyList<ObsMediaAction> AllMediaActions { get; } =
        [ObsMediaAction.Play, ObsMediaAction.Pause, ObsMediaAction.Stop, ObsMediaAction.Restart];

    private ObsSceneMode   _selectedMode  = ObsSceneMode.Switch;
    private string?        _selectedScene;
    private string?        _selectedSource;
    private ObsContentType _contentType   = ObsContentType.Text;
    private string         _textContent   = string.Empty;
    private string         _filePath      = string.Empty;
    private ObsMediaAction _mediaAction   = ObsMediaAction.Play;

    public ObsSceneMode SelectedMode
    {
        get => _selectedMode;
        set { if (_selectedMode != value) { _selectedMode = value; OnPropertyChanged(); } }
    }
    public string? SelectedScene
    {
        get => _selectedScene;
        set { if (_selectedScene != value) { _selectedScene = value; OnPropertyChanged(); } }
    }
    public string? SelectedSource
    {
        get => _selectedSource;
        set { if (_selectedSource != value) { _selectedSource = value; OnPropertyChanged(); } }
    }
    public ObsContentType ContentType
    {
        get => _contentType;
        set { if (_contentType != value) { _contentType = value; OnPropertyChanged(); } }
    }
    public string TextContent
    {
        get => _textContent;
        set { if (_textContent != value) { _textContent = value; OnPropertyChanged(); } }
    }
    public string FilePath
    {
        get => _filePath;
        set { if (_filePath != value) { _filePath = value; OnPropertyChanged(); } }
    }
    public ObsMediaAction MediaAction
    {
        get => _mediaAction;
        set { if (_mediaAction != value) { _mediaAction = value; OnPropertyChanged(); } }
    }

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider, ILogService logger, CancellationToken cancellationToken)
    {
        TryApplyContextInput<string>(nameof(TextContent), v => TextContent = v);
        TryApplyContextInput<string>(nameof(FilePath), v => FilePath = v);

        var obs = serviceProvider.GetRequiredService<IObsService>();
        if (!obs.IsConnected || string.IsNullOrEmpty(SelectedSource))
        {
            await logger.LogWarningAsync(Name, "[OBS] Динамический контент: нет подключения или не выбран источник.")
                .ConfigureAwait(false);
            return false;
        }

        if (SelectedMode == ObsSceneMode.CheckActive)
            return await ExecuteCheckActiveAsync(obs, logger, cancellationToken).ConfigureAwait(false);

        switch (ContentType)
        {
            case ObsContentType.Text:
                await obs.SetInputTextAsync(SelectedSource, TextContent, cancellationToken).ConfigureAwait(false);
                await logger.LogInfoAsync(Name,
                    $"[OBS] Текст '{SelectedSource}' → '{TextContent[..Math.Min(TextContent.Length, 50)]}'")
                    .ConfigureAwait(false);
                break;
            case ObsContentType.Image:
                await obs.SetInputFilePathAsync(SelectedSource, FilePath, cancellationToken).ConfigureAwait(false);
                await logger.LogInfoAsync(Name,
                    $"[OBS] Изображение '{SelectedSource}' → '{FilePath}'").ConfigureAwait(false);
                break;
            case ObsContentType.Media:
                string action = MediaAction switch
                {
                    ObsMediaAction.Play    => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PLAY",
                    ObsMediaAction.Pause   => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PAUSE",
                    ObsMediaAction.Stop    => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_STOP",
                    ObsMediaAction.Restart => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_RESTART",
                    _                       => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_NONE"
                };
                await obs.TriggerMediaActionAsync(SelectedSource, action, cancellationToken).ConfigureAwait(false);
                await logger.LogInfoAsync(Name, $"[OBS] Медиа '{SelectedSource}' → {MediaAction}").ConfigureAwait(false);
                break;
        }
        return true;
    }

    private async Task<bool> ExecuteCheckActiveAsync(IObsService obs, ILogService logger, CancellationToken ct)
    {
        bool result;
        switch (ContentType)
        {
            case ObsContentType.Text:
                string curText = await obs.GetInputTextAsync(SelectedSource!, ct).ConfigureAwait(false);
                result = string.Equals(curText, TextContent, StringComparison.Ordinal);
                await logger.LogInfoAsync(Name,
                    $"[OBS] Проверка текста '{SelectedSource}': совпадает={result}").ConfigureAwait(false);
                break;
            case ObsContentType.Image:
                string curPath = await obs.GetInputFilePathAsync(SelectedSource!, ct).ConfigureAwait(false);
                result = string.Equals(curPath, FilePath, StringComparison.OrdinalIgnoreCase);
                await logger.LogInfoAsync(Name,
                    $"[OBS] Проверка пути '{SelectedSource}': совпадает={result}").ConfigureAwait(false);
                break;
            case ObsContentType.Media:
                string state = await obs.GetMediaStateAsync(SelectedSource!, ct).ConfigureAwait(false);
                result = MediaAction switch
                {
                    ObsMediaAction.Play    => state == "OBS_MEDIA_STATE_PLAYING",
                    ObsMediaAction.Pause   => state == "OBS_MEDIA_STATE_PAUSED",
                    ObsMediaAction.Stop    => state is "OBS_MEDIA_STATE_STOPPED" or "OBS_MEDIA_STATE_ENDED",
                    ObsMediaAction.Restart => state == "OBS_MEDIA_STATE_PLAYING",
                    _                       => false
                };
                await logger.LogInfoAsync(Name,
                    $"[OBS] Медиа '{SelectedSource}': состояние='{state}', результат={result}").ConfigureAwait(false);
                break;
            default:
                result = false;
                break;
        }
        return result;
    }
}
