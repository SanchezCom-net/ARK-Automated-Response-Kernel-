using ARK.UI.Core.Bus;
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

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        var obs = NodeServices!.GetRequiredService<IObsService>();
        if (!obs.IsConnected)
        {
            await NodeLogger!.LogWarningAsync(Name, "[OBS] Действие пропущено: нет подключения к OBS Studio.")
                .ConfigureAwait(false);
            return NodeResult.Failure("OBS не подключён.");
        }

        if (SelectedMode == ObsSceneMode.CheckActive)
        {
            bool isActive = StreamTarget switch
            {
                ObsStreamTarget.Recording    => await obs.IsRecordingAsync(ct).ConfigureAwait(false),
                ObsStreamTarget.Streaming    => await obs.IsStreamingAsync(ct).ConfigureAwait(false),
                ObsStreamTarget.ReplayBuffer => await obs.IsReplayBufferActiveAsync(ct).ConfigureAwait(false),
                _                             => false
            };
            bool result = StreamAction switch
            {
                ObsStreamAction.Start  => isActive,
                ObsStreamAction.Stop   => !isActive,
                _                       => isActive
            };
            await NodeLogger!.LogInfoAsync(Name,
                $"[OBS] Проверка {StreamTarget}: активен={isActive} → результат={result}").ConfigureAwait(false);
            return result ? NodeResult.Success(null) : NodeResult.Failure($"{StreamTarget} не в ожидаемом состоянии.");
        }

        Task switchTask = (StreamTarget, StreamAction) switch
        {
            (ObsStreamTarget.Recording,    ObsStreamAction.Start)  => obs.StartRecordingAsync(ct),
            (ObsStreamTarget.Recording,    ObsStreamAction.Stop)   => obs.StopRecordingAsync(ct),
            (ObsStreamTarget.Recording,    ObsStreamAction.Toggle) => obs.ToggleRecordingAsync(ct),
            (ObsStreamTarget.Streaming,    ObsStreamAction.Start)  => obs.StartStreamingAsync(ct),
            (ObsStreamTarget.Streaming,    ObsStreamAction.Stop)   => obs.StopStreamingAsync(ct),
            (ObsStreamTarget.Streaming,    ObsStreamAction.Toggle) => obs.ToggleStreamingAsync(ct),
            (ObsStreamTarget.ReplayBuffer, ObsStreamAction.Start)  => obs.StartReplayBufferAsync(ct),
            (ObsStreamTarget.ReplayBuffer, ObsStreamAction.Stop)   => obs.StopReplayBufferAsync(ct),
            (ObsStreamTarget.ReplayBuffer, ObsStreamAction.Toggle) => obs.ToggleReplayBufferAsync(ct),
            (ObsStreamTarget.ReplayBuffer, ObsStreamAction.Save)   => obs.SaveReplayBufferAsync(ct),
            _                                                        => obs.ToggleRecordingAsync(ct)
        };
        await switchTask.ConfigureAwait(false);
        await NodeLogger!.LogInfoAsync(Name, $"[OBS] {StreamTarget} / {StreamAction} — выполнено.").ConfigureAwait(false);
        return NodeResult.Success(null);
    }
}
