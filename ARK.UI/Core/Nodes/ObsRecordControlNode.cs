using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class ObsRecordControlNode : BaseNode
{
    public static readonly ObsRecordActionType[] AllActions = Enum.GetValues<ObsRecordActionType>();

    public static IReadOnlyList<ObsSceneMode> AllModes { get; } =
        [ObsSceneMode.Switch, ObsSceneMode.CheckActive];

    private ObsSceneMode _selectedMode = ObsSceneMode.Switch;
    public ObsSceneMode SelectedMode
    {
        get => _selectedMode;
        set { if (_selectedMode != value) { _selectedMode = value; OnPropertyChanged(); } }
    }

    private ObsRecordActionType _action = ObsRecordActionType.Toggle;
    public ObsRecordActionType Action
    {
        get => _action;
        set { if (_action != value) { _action = value; OnPropertyChanged(); } }
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

        if (SelectedMode == ObsSceneMode.Switch)
        {
            var task = Action switch
            {
                ObsRecordActionType.Start  => obs.StartRecordingAsync(ct),
                ObsRecordActionType.Stop   => obs.StopRecordingAsync(ct),
                ObsRecordActionType.Toggle => obs.ToggleRecordingAsync(ct),
                _                          => obs.ToggleRecordingAsync(ct)
            };
            await task.ConfigureAwait(false);
            return NodeResult.Success(null);
        }

        var isRecording = await obs.IsRecordingAsync(ct).ConfigureAwait(false);
        var isMatch = Action switch
        {
            ObsRecordActionType.Start  => isRecording,
            ObsRecordActionType.Stop   => !isRecording,
            ObsRecordActionType.Toggle => isRecording,
            _                          => isRecording
        };
        await NodeLogger!.LogInfoAsync(Name,
            $"[OBS] Проверка записи (Action={Action}): {(isMatch ? "УСЛОВИЕ ВЫПОЛНЕНО" : "УСЛОВИЕ НЕ ВЫПОЛНЕНО")} (Запись идёт: {isRecording})")
            .ConfigureAwait(false);
        return isMatch ? NodeResult.Success(null) : NodeResult.Failure("Условие записи OBS не выполнено.");
    }
}
