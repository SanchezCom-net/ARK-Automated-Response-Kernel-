using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes.OBS;

public sealed class OBS_AudioManagerNode : BaseNode
{
    public static IReadOnlyList<ObsSceneMode>   AllModes        { get; } = [ObsSceneMode.Switch, ObsSceneMode.CheckActive];
    public static IReadOnlyList<ObsAudioAction> AllAudioActions { get; } =
        [ObsAudioAction.Mute, ObsAudioAction.Unmute, ObsAudioAction.ToggleMute, ObsAudioAction.SetVolume];

    private ObsSceneMode   _selectedMode   = ObsSceneMode.Switch;
    private string?        _selectedInput;
    private ObsAudioAction _audioAction    = ObsAudioAction.ToggleMute;
    private float          _targetVolumeDb = -10f;

    public ObsSceneMode SelectedMode
    {
        get => _selectedMode;
        set { if (_selectedMode != value) { _selectedMode = value; OnPropertyChanged(); } }
    }
    public string? SelectedInput
    {
        get => _selectedInput;
        set { if (_selectedInput != value) { _selectedInput = value; OnPropertyChanged(); } }
    }
    public ObsAudioAction AudioAction
    {
        get => _audioAction;
        set { if (_audioAction != value) { _audioAction = value; OnPropertyChanged(); } }
    }
    public float TargetVolumeDb
    {
        get => _targetVolumeDb;
        set { if (MathF.Abs(_targetVolumeDb - value) > 0.001f) { _targetVolumeDb = value; OnPropertyChanged(); } }
    }

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider, ILogService logger, CancellationToken cancellationToken)
    {
        var obs = serviceProvider.GetRequiredService<IObsService>();
        if (!obs.IsConnected || string.IsNullOrEmpty(SelectedInput))
        {
            await logger.LogWarningAsync(Name, "[OBS] Аудио: нет подключения или не выбран источник.").ConfigureAwait(false);
            return false;
        }

        if (SelectedMode == ObsSceneMode.CheckActive)
        {
            if (AudioAction == ObsAudioAction.SetVolume)
            {
                float cur = await obs.GetInputVolumeDbAsync(SelectedInput, cancellationToken).ConfigureAwait(false);
                bool match = MathF.Abs(cur - TargetVolumeDb) < 0.5f;
                await logger.LogInfoAsync(Name,
                    $"[OBS] Проверка громкости '{SelectedInput}': {cur:F1} дБ, ожидается {TargetVolumeDb:F1} дБ, совпадает={match}")
                    .ConfigureAwait(false);
                return match;
            }
            bool isMuted = await obs.IsInputMutedAsync(SelectedInput, cancellationToken).ConfigureAwait(false);
            bool result  = AudioAction switch
            {
                ObsAudioAction.Mute       => isMuted,
                ObsAudioAction.Unmute     => !isMuted,
                ObsAudioAction.ToggleMute => isMuted,
                _                          => isMuted
            };
            await logger.LogInfoAsync(Name,
                $"[OBS] Проверка mute '{SelectedInput}': заглушён={isMuted}, результат={result}").ConfigureAwait(false);
            return result;
        }

        switch (AudioAction)
        {
            case ObsAudioAction.Mute:
                await obs.SetInputMuteAsync(SelectedInput, true, cancellationToken).ConfigureAwait(false);
                await logger.LogInfoAsync(Name, $"[OBS] '{SelectedInput}' — заглушён.").ConfigureAwait(false);
                break;
            case ObsAudioAction.Unmute:
                await obs.SetInputMuteAsync(SelectedInput, false, cancellationToken).ConfigureAwait(false);
                await logger.LogInfoAsync(Name, $"[OBS] '{SelectedInput}' — включён.").ConfigureAwait(false);
                break;
            case ObsAudioAction.ToggleMute:
                await obs.ToggleInputMuteAsync(SelectedInput, cancellationToken).ConfigureAwait(false);
                await logger.LogInfoAsync(Name, $"[OBS] '{SelectedInput}' — mute переключён.").ConfigureAwait(false);
                break;
            case ObsAudioAction.SetVolume:
                await obs.SetInputVolumeDbAsync(SelectedInput, TargetVolumeDb, cancellationToken).ConfigureAwait(false);
                await logger.LogInfoAsync(Name, $"[OBS] '{SelectedInput}' — громкость {TargetVolumeDb:F1} дБ.").ConfigureAwait(false);
                break;
        }
        return true;
    }
}
