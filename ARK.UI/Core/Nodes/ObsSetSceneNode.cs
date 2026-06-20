using ARK.UI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public sealed class ObsSetSceneNode : BaseNode
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
            await logger.LogInfoAsync(Name, $"[OBS] Сцена успешно переключена на '{SceneName}'.")
                .ConfigureAwait(false);
            return true;
        }

        // CheckActive: проверяем совпадение текущей сцены — возвращаем true/false для ветвления графа
        var current = await obs.GetCurrentSceneAsync(cancellationToken).ConfigureAwait(false);
        var isMatch = string.Equals(current, SceneName, StringComparison.OrdinalIgnoreCase);
        await logger.LogInfoAsync(Name,
            $"[OBS] Проверка сцены '{SceneName}': {(isMatch ? "АКТИВНА" : "НЕАКТИВНА")} (Текущая: '{current}')")
            .ConfigureAwait(false);
        return isMatch;
    }
}
