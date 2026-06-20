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

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        var obs = serviceProvider.GetRequiredService<IObsService>();
        if (!obs.IsConnected)
        {
            await logger.LogWarningAsync(Name, "[OBS] Действие пропущено: нет подключения к OBS Studio.")
                .ConfigureAwait(false);
            return false;
        }

        if (SelectedMode == ObsSceneMode.Switch)
        {
            var task = Action switch
            {
                ObsRecordActionType.Start  => obs.StartRecordingAsync(cancellationToken),
                ObsRecordActionType.Stop   => obs.StopRecordingAsync(cancellationToken),
                ObsRecordActionType.Toggle => obs.ToggleRecordingAsync(cancellationToken),
                _                          => obs.ToggleRecordingAsync(cancellationToken)
            };
            await task.ConfigureAwait(false);
            return true;
        }

        // CheckActive: Start → запись идёт; Stop → запись остановлена; Toggle → текущее состояние
        var isRecording = await obs.IsRecordingAsync(cancellationToken).ConfigureAwait(false);
        var isMatch = Action switch
        {
            ObsRecordActionType.Start  => isRecording,
            ObsRecordActionType.Stop   => !isRecording,
            ObsRecordActionType.Toggle => isRecording,
            _                          => isRecording
        };
        await logger.LogInfoAsync(Name,
            $"[OBS] Проверка записи (Action={Action}): {(isMatch ? "УСЛОВИЕ ВЫПОЛНЕНО" : "УСЛОВИЕ НЕ ВЫПОЛНЕНО")} (Запись идёт: {isRecording})")
            .ConfigureAwait(false);
        return isMatch;
    }
}
