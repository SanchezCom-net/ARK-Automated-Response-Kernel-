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
            await obs.SetInputMuteAsync(InputName, IsMuted, cancellationToken).ConfigureAwait(false);
            await logger.LogInfoAsync(Name,
                $"[OBS] Источник '{InputName}' — {(IsMuted ? "заглушён" : "включён")}")
                .ConfigureAwait(false);
            return true;
        }

        // CheckActive: сравниваем текущее состояние mute с ожидаемым для ветвления графа
        var isMuted = await obs.IsInputMutedAsync(InputName, cancellationToken).ConfigureAwait(false);
        var isMatch = isMuted == IsMuted;
        await logger.LogInfoAsync(Name,
            $"[OBS] Проверка mute '{InputName}': {(isMatch ? "СОВПАДАЕТ" : "НЕ СОВПАДАЕТ")} (Заглушён: {isMuted}, Ожидается: {IsMuted})")
            .ConfigureAwait(false);
        return isMatch;
    }
}
