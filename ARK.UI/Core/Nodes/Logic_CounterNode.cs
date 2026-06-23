using ARK.UI.Core.Bus;

namespace ARK.UI.Core.Nodes;

public sealed class Logic_CounterNode : BaseNode
{
    private int _limit = 3;
    private int _current;

    public int Limit
    {
        get => _limit;
        set { if (_limit != value) { _limit = value; OnPropertyChanged(); } }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public int Current => _current;

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        _current++;
        LastOutputValue = _current;
        bool reached = _current >= Limit;
        await NodeLogger!.LogInfoAsync(Name,
            $"[Counter] {_current}/{Limit} — {(reached ? "лимит достигнут" : "продолжаем")}").ConfigureAwait(false);
        return reached ? NodeResult.Success(null) : NodeResult.Failure("Лимит не достигнут.");
    }
}
