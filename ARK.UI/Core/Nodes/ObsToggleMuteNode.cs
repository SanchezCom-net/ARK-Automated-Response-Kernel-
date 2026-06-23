using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class ObsToggleMuteNode : BaseNode
{
    public static IReadOnlyList<ObsSceneMode> AllModes { get; } =
        [ObsSceneMode.Switch, ObsSceneMode.CheckActive];

    private ObsSceneMode _selectedMode = ObsSceneMode.Switch;
    public ObsSceneMode SelectedMode
    {
        get => _selectedMode;
        set { if (_selectedMode != value) { _selectedMode = value; OnPropertyChanged(); } }
    }

    private string _inputName = string.Empty;
    private bool   _isMuted;

    public string InputName
    {
        get => _inputName;
        set { if (_inputName != value) { _inputName = value; OnPropertyChanged(); } }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set { if (_isMuted != value) { _isMuted = value; OnPropertyChanged(); } }
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
            await obs.SetInputMuteAsync(InputName, IsMuted, ct).ConfigureAwait(false);
            await NodeLogger!.LogInfoAsync(Name,
                $"[OBS] Источник '{InputName}' — {(IsMuted ? "заглушён" : "включён")}")
                .ConfigureAwait(false);
            return NodeResult.Success(null);
        }

        var isMuted = await obs.IsInputMutedAsync(InputName, ct).ConfigureAwait(false);
        var isMatch = isMuted == IsMuted;
        await NodeLogger!.LogInfoAsync(Name,
            $"[OBS] Проверка mute '{InputName}': {(isMatch ? "СОВПАДАЕТ" : "НЕ СОВПАДАЕТ")} (Заглушён: {isMuted}, Ожидается: {IsMuted})")
            .ConfigureAwait(false);
        return isMatch ? NodeResult.Success(null) : NodeResult.Failure("Mute-состояние не совпадает.");
    }
}
