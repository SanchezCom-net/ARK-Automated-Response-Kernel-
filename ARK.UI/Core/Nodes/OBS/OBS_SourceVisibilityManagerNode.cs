using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes.OBS;

public sealed class OBS_SourceVisibilityManagerNode : BaseNode, IObsCascadeNode
{
    public static IReadOnlyList<ObsSceneMode>        AllModes   { get; } = [ObsSceneMode.Switch, ObsSceneMode.CheckActive];
    public static IReadOnlyList<ObsVisibilityTarget> AllTargets { get; } = [ObsVisibilityTarget.Source, ObsVisibilityTarget.Filter];
    public static IReadOnlyList<ObsVisibilityAction> AllActions { get; } = [ObsVisibilityAction.Show, ObsVisibilityAction.Hide, ObsVisibilityAction.Toggle];

    private ObsSceneMode        _selectedMode     = ObsSceneMode.Switch;
    private string?             _selectedScene;
    private string?             _selectedSource;
    private string?             _selectedFilter;
    private ObsVisibilityTarget _targetType       = ObsVisibilityTarget.Source;
    private ObsVisibilityAction _visibilityAction = ObsVisibilityAction.Show;

    public ObsSceneMode SelectedMode
    {
        get => _selectedMode;
        set { if (_selectedMode != value) { _selectedMode = value; OnPropertyChanged(); } }
    }
    public string? SelectedScene
    {
        get => _selectedScene;
        set { if (_selectedScene != value) { _selectedScene = value; OnPropertyChanged(); } }
    }
    public string? SelectedSource
    {
        get => _selectedSource;
        set { if (_selectedSource != value) { _selectedSource = value; OnPropertyChanged(); } }
    }
    public string? SelectedFilter
    {
        get => _selectedFilter;
        set { if (_selectedFilter != value) { _selectedFilter = value; OnPropertyChanged(); } }
    }
    public ObsVisibilityTarget TargetType
    {
        get => _targetType;
        set { if (_targetType != value) { _targetType = value; OnPropertyChanged(); } }
    }
    public ObsVisibilityAction VisibilityAction
    {
        get => _visibilityAction;
        set { if (_visibilityAction != value) { _visibilityAction = value; OnPropertyChanged(); } }
    }

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        var obs = NodeServices!.GetRequiredService<IObsService>();
        if (!obs.IsConnected)
            return NodeResult.Failure("OBS не подключён.");
        bool ok = TargetType == ObsVisibilityTarget.Source
            ? await ExecuteSourceAsync(obs, NodeLogger!, ct).ConfigureAwait(false)
            : await ExecuteFilterAsync(obs, NodeLogger!, ct).ConfigureAwait(false);
        return ok ? NodeResult.Success(null) : NodeResult.Failure("Операция OBS видимости не выполнена.");
    }

    private async Task<bool> ExecuteSourceAsync(IObsService obs, ILogService logger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(SelectedScene) || string.IsNullOrEmpty(SelectedSource)) return false;
        int id = await obs.GetSceneItemIdAsync(SelectedScene, SelectedSource, ct).ConfigureAwait(false);
        if (id < 0) return false;

        bool current = await obs.GetSceneItemEnabledAsync(SelectedScene, id, ct).ConfigureAwait(false);

        if (SelectedMode == ObsSceneMode.CheckActive)
        {
            bool res = VisibilityAction == ObsVisibilityAction.Show ? current
                     : VisibilityAction == ObsVisibilityAction.Hide ? !current : current;
            await logger.LogInfoAsync(Name,
                $"[OBS] Источник '{SelectedSource}': виден={current} → результат={res}").ConfigureAwait(false);
            return res;
        }

        bool newState = VisibilityAction switch
        {
            ObsVisibilityAction.Show   => true,
            ObsVisibilityAction.Hide   => false,
            ObsVisibilityAction.Toggle => !current,
            _                           => !current
        };
        await obs.SetSceneItemEnabledAsync(SelectedScene, id, newState, ct).ConfigureAwait(false);
        await logger.LogInfoAsync(Name,
            $"[OBS] Источник '{SelectedSource}' → {(newState ? "показан" : "скрыт")}.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExecuteFilterAsync(IObsService obs, ILogService logger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(SelectedSource) || string.IsNullOrEmpty(SelectedFilter)) return false;
        bool current = await obs.GetSourceFilterEnabledAsync(SelectedSource, SelectedFilter, ct).ConfigureAwait(false);

        if (SelectedMode == ObsSceneMode.CheckActive)
        {
            bool res = VisibilityAction == ObsVisibilityAction.Show ? current
                     : VisibilityAction == ObsVisibilityAction.Hide ? !current : current;
            await logger.LogInfoAsync(Name,
                $"[OBS] Фильтр '{SelectedFilter}': активен={current} → результат={res}").ConfigureAwait(false);
            return res;
        }

        bool newState = VisibilityAction switch
        {
            ObsVisibilityAction.Show   => true,
            ObsVisibilityAction.Hide   => false,
            ObsVisibilityAction.Toggle => !current,
            _                           => !current
        };
        await obs.SetSourceFilterEnabledAsync(SelectedSource, SelectedFilter, newState, ct).ConfigureAwait(false);
        await logger.LogInfoAsync(Name,
            $"[OBS] Фильтр '{SelectedFilter}' у '{SelectedSource}' → {(newState ? "включён" : "выключен")}.").ConfigureAwait(false);
        return true;
    }
}
