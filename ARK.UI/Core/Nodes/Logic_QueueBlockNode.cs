using ARK.UI.Core.Bus;

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

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        await NodeLogger!.LogInfoAsync(Name, "[ШЛЮЗ] Барьер синхронизации пройден.")
            .ConfigureAwait(false);
        return NodeResult.Success(null);
    }
}
