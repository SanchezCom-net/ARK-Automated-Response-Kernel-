using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes.OBS;

public sealed class OBS_StreamAndRecordManagerNode : BaseNode
{
    public static IReadOnlyList<ObsSceneMode>    AllModes   { get; } = [ObsSceneMode.Switch, ObsSceneMode.CheckActive];
    public static IReadOnlyList<ObsStreamTarget> AllTargets { get; } =
        [ObsStreamTarget.Recording, ObsStreamTarget.Streaming, ObsStreamTarget.ReplayBuffer];
    public static IReadOnlyList<ObsStreamAction> AllActions { get; } =
        [ObsStreamAction.Start, ObsStreamAction.Stop, ObsStreamAction.Toggle, ObsStreamAction.Save];

    private ObsSceneMode    _selectedMode  = ObsSceneMode.Switch;
    private ObsStreamTarget _streamTarget  = ObsStreamTarget.Recording;
    private ObsStreamAction _streamAction  = ObsStreamAction.Toggle;

    public ObsSceneMode SelectedMode
    {
        get => _selectedMode;
        set { if (_selectedMode != value) { _selectedMode = value; OnPropertyChanged(); } }
    }
    public ObsStreamTarget StreamTarget
    {
        get => _streamTarget;
        set { if (_streamTarget != value) { _streamTarget = value; OnPropertyChanged(); } }
    }
    public ObsStreamAction StreamAction
    {
        get => _streamAction;
        set { if (_streamAction != value) { _streamAction = value; OnPropertyChanged(); } }
    }

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider, ILogService logger, CancellationToken cancellationToken)
    {
        var obs = serviceProvider.GetRequiredService<IObsService>();
        if (!obs.IsConnected)
        {
            await logger.LogWarningAsync(Name, "[OBS] Действие пропущено: нет подключения к OBS Studio.")
                .ConfigureAwait(false);
            return false;
        }

        if (SelectedMode == ObsSceneMode.CheckActive)
        {
            bool isActive = StreamTarget switch
            {
                ObsStreamTarget.Recording    => await obs.IsRecordingAsync(cancellationToken).ConfigureAwait(false),
                ObsStreamTarget.Streaming    => await obs.IsStreamingAsync(cancellationToken).ConfigureAwait(false),
                ObsStreamTarget.ReplayBuffer => await obs.IsReplayBufferActiveAsync(cancellationToken).ConfigureAwait(false),
                _                             => false
            };
            bool result = StreamAction switch
            {
                ObsStreamAction.Start  => isActive,
                ObsStreamAction.Stop   => !isActive,
                _                       => isActive
            };
            await logger.LogInfoAsync(Name,
                $"[OBS] Проверка {StreamTarget}: активен={isActive} → результат={result}").ConfigureAwait(false);
            return result;
        }

        Task switchTask = (StreamTarget, StreamAction) switch
        {
            (ObsStreamTarget.Recording,    ObsStreamAction.Start)  => obs.StartRecordingAsync(cancellationToken),
            (ObsStreamTarget.Recording,    ObsStreamAction.Stop)   => obs.StopRecordingAsync(cancellationToken),
            (ObsStreamTarget.Recording,    ObsStreamAction.Toggle) => obs.ToggleRecordingAsync(cancellationToken),
            (ObsStreamTarget.Streaming,    ObsStreamAction.Start)  => obs.StartStreamingAsync(cancellationToken),
            (ObsStreamTarget.Streaming,    ObsStreamAction.Stop)   => obs.StopStreamingAsync(cancellationToken),
            (ObsStreamTarget.Streaming,    ObsStreamAction.Toggle) => obs.ToggleStreamingAsync(cancellationToken),
            (ObsStreamTarget.ReplayBuffer, ObsStreamAction.Start)  => obs.StartReplayBufferAsync(cancellationToken),
            (ObsStreamTarget.ReplayBuffer, ObsStreamAction.Stop)   => obs.StopReplayBufferAsync(cancellationToken),
            (ObsStreamTarget.ReplayBuffer, ObsStreamAction.Toggle) => obs.ToggleReplayBufferAsync(cancellationToken),
            (ObsStreamTarget.ReplayBuffer, ObsStreamAction.Save)   => obs.SaveReplayBufferAsync(cancellationToken),
            _                                                        => obs.ToggleRecordingAsync(cancellationToken)
        };
        await switchTask.ConfigureAwait(false);
        await logger.LogInfoAsync(Name, $"[OBS] {StreamTarget} / {StreamAction} — выполнено.").ConfigureAwait(false);
        return true;
    }
}
