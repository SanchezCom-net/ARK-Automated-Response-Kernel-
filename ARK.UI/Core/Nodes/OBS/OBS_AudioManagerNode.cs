using ARK.UI.Core.Bus;
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

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        var obs = NodeServices!.GetRequiredService<IObsService>();
        if (!obs.IsConnected || string.IsNullOrEmpty(SelectedInput))
        {
            await NodeLogger!.LogWarningAsync(Name, "[OBS] Аудио: нет подключения или не выбран источник.").ConfigureAwait(false);
            return NodeResult.Failure("OBS не подключён или источник не выбран.");
        }

        if (SelectedMode == ObsSceneMode.CheckActive)
        {
            if (AudioAction == ObsAudioAction.SetVolume)
            {
                float cur = await obs.GetInputVolumeDbAsync(SelectedInput, ct).ConfigureAwait(false);
                bool match = MathF.Abs(cur - TargetVolumeDb) < 0.5f;
                await NodeLogger!.LogInfoAsync(Name,
                    $"[OBS] Проверка громкости '{SelectedInput}': {cur:F1} дБ, ожидается {TargetVolumeDb:F1} дБ, совпадает={match}")
                    .ConfigureAwait(false);
                return match ? NodeResult.Success(null) : NodeResult.Failure("Громкость не совпадает.");
            }
            bool isMuted = await obs.IsInputMutedAsync(SelectedInput, ct).ConfigureAwait(false);
            bool result  = AudioAction switch
            {
                ObsAudioAction.Mute       => isMuted,
                ObsAudioAction.Unmute     => !isMuted,
                ObsAudioAction.ToggleMute => isMuted,
                _                          => isMuted
            };
            await NodeLogger!.LogInfoAsync(Name,
                $"[OBS] Проверка mute '{SelectedInput}': заглушён={isMuted}, результат={result}").ConfigureAwait(false);
            return result ? NodeResult.Success(null) : NodeResult.Failure("Mute-статус не совпадает.");
        }

        switch (AudioAction)
        {
            case ObsAudioAction.Mute:
                await obs.SetInputMuteAsync(SelectedInput, true, ct).ConfigureAwait(false);
                await NodeLogger!.LogInfoAsync(Name, $"[OBS] '{SelectedInput}' — заглушён.").ConfigureAwait(false);
                break;
            case ObsAudioAction.Unmute:
                await obs.SetInputMuteAsync(SelectedInput, false, ct).ConfigureAwait(false);
                await NodeLogger!.LogInfoAsync(Name, $"[OBS] '{SelectedInput}' — включён.").ConfigureAwait(false);
                break;
            case ObsAudioAction.ToggleMute:
                await obs.ToggleInputMuteAsync(SelectedInput, ct).ConfigureAwait(false);
                await NodeLogger!.LogInfoAsync(Name, $"[OBS] '{SelectedInput}' — mute переключён.").ConfigureAwait(false);
                break;
            case ObsAudioAction.SetVolume:
                await obs.SetInputVolumeDbAsync(SelectedInput, TargetVolumeDb, ct).ConfigureAwait(false);
                await NodeLogger!.LogInfoAsync(Name, $"[OBS] '{SelectedInput}' — громкость {TargetVolumeDb:F1} дБ.").ConfigureAwait(false);
                break;
        }
        return NodeResult.Success(null);
    }
}
