using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Nodes;

public sealed class Logic_QueueBlockNode : BaseNode
{
    public override int CardBodyWidth { get; } = 72;

    private bool _waitFullChain = true;
    public bool WaitFullChain
    {
        get => _waitFullChain;
        set { if (_waitFullChain != value) { _waitFullChain = value; OnPropertyChanged(); } }
    }

    private int _waitNodesCount = 1;
    public int WaitNodesCount
    {
        get => _waitNodesCount;
        set { if (_waitNodesCount != value) { _waitNodesCount = value; OnPropertyChanged(); } }
    }

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        // NodeEngine обеспечивает ожидание ДО вызова этого метода:
        //   WaitFullChain=true  → Task.WhenAll всех параллельных задач предыдущего шага.
        //   WaitFullChain=false → задержка до достижения WaitNodesCount завершений.
        // Нода служит визуальным маркером барьера и просто пропускает управление дальше.
        await logger.LogInfoAsync(Name, "[ШЛЮЗ] Барьер синхронизации пройден.")
            .ConfigureAwait(false);
        return true;
    }
}
