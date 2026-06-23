using ARK.UI.Core.Bus;
using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Nodes;

public sealed class DelayNode : BaseNode
{
    public override string DefaultDataInputPropertyName => nameof(DelayMilliseconds);

    private int  _delayMs    = 1000;
    private bool _isRandom   = false;
    private int  _minDelayMs = 500;
    private int  _maxDelayMs = 1500;

    public int DelayMilliseconds
    {
        get => _delayMs;
        set { if (_delayMs != value) { _delayMs = value; OnPropertyChanged(); } }
    }

    public bool IsDelayRandom
    {
        get => _isRandom;
        set { if (_isRandom != value) { _isRandom = value; OnPropertyChanged(); } }
    }

    public int MinDelayMilliseconds
    {
        get => _minDelayMs;
        set { if (_minDelayMs != value) { _minDelayMs = value; OnPropertyChanged(); } }
    }

    public int MaxDelayMilliseconds
    {
        get => _maxDelayMs;
        set { if (_maxDelayMs != value) { _maxDelayMs = value; OnPropertyChanged(); } }
    }

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _raw))
        {
            if (_raw is int _i)        DelayMilliseconds = _i;
            else if (_raw is string _s && int.TryParse(_s, out int _p)) DelayMilliseconds = _p;
            else _ = NodeLogger!.LogWarningAsync(Name,
                $"[ТАЙМЕР] Невалидное значение задержки: '{_raw}'. Используется стандартная задержка.");
        }

        // Smart Fields V3.6: метаданные шины переопределяют значение из UI
        if (TryGetMappedMetadata(nameof(DelayMilliseconds), inputPacket, out var metaDelay)
            && int.TryParse(metaDelay, out int parsedDelay))
            DelayMilliseconds = parsedDelay;

        if (TryGetMappedMetadata(nameof(MinDelayMilliseconds), inputPacket, out var metaMin)
            && int.TryParse(metaMin, out int parsedMin))
            MinDelayMilliseconds = parsedMin;

        if (TryGetMappedMetadata(nameof(MaxDelayMilliseconds), inputPacket, out var metaMax)
            && int.TryParse(metaMax, out int parsedMax))
            MaxDelayMilliseconds = parsedMax;

        int finalDelay;
        if (IsDelayRandom)
        {
            int min = Math.Min(MinDelayMilliseconds, MaxDelayMilliseconds);
            int max = Math.Max(MinDelayMilliseconds, MaxDelayMilliseconds);
            finalDelay = Random.Shared.Next(min, max + 1);
        }
        else
        {
            finalDelay = DelayMilliseconds;
        }

        LastOutputValue = finalDelay;

        await NodeLogger!.LogInfoAsync(Name,
            $"[ТАЙМЕР] Запущено ожидание задержки: {finalDelay} мс.").ConfigureAwait(false);

        // Chunked delay: сбрасываем Watchdog каждые 5 сек, чтобы Orchestrator не объявил ноду ZOMBIE
        const int ChunkMs = 5_000;
        int remaining = finalDelay;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            ResetWatchdogTimer();
            int chunk = Math.Min(remaining, ChunkMs);
            await Task.Delay(chunk, ct).ConfigureAwait(false);
            remaining -= chunk;
        }

        var _sid = inputPacket?.SessionId ?? Guid.NewGuid();
        var _out = DataBusPacket.Text(_sid);
        DataBus?.Set(_out.SessionId, _out.DataId, finalDelay);
        return NodeResult.Success(_out);
    }
}
