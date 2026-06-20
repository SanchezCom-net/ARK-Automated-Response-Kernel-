using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes.OBS;

public sealed class OBS_SceneManagerNode : BaseNode
{
    public static IReadOnlyList<ObsSceneMode> AllModes { get; } =
        [ObsSceneMode.Switch, ObsSceneMode.CheckActive];

    private ObsSceneMode _selectedMode = ObsSceneMode.Switch;
    public ObsSceneMode SelectedMode
    {
        get => _selectedMode;
        set { if (_selectedMode != value) { _selectedMode = value; OnPropertyChanged(); } }
    }

    private string _sceneName = string.Empty;
    public string SceneName
    {
        get => _sceneName;
        set { if (_sceneName != value) { _sceneName = value; OnPropertyChanged(); } }
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
            await obs.SetCurrentProgramSceneAsync(SceneName, cancellationToken).ConfigureAwait(false);
            await logger.LogInfoAsync(Name, $"[OBS] Сцена переключена → '{SceneName}'.").ConfigureAwait(false);
            return true;
        }

        var current = await obs.GetCurrentSceneAsync(cancellationToken).ConfigureAwait(false);
        var isMatch = string.Equals(current, SceneName, StringComparison.OrdinalIgnoreCase);
        await logger.LogInfoAsync(Name,
            $"[OBS] Проверка сцены '{SceneName}': {(isMatch ? "АКТИВНА" : "НЕАКТИВНА")} (текущая: '{current}')")
            .ConfigureAwait(false);
        return isMatch;
    }
}
