using System.Collections.Concurrent;
using ARK.UI.Core.Bus;

namespace ARK.UI.Core.Nodes;

public enum SynchronizerMode
{
    Accumulator, // Накопитель: ждём ровно TargetCount входящих пакетов
    Timer        // Таймер: ждём TimeoutSeconds, затем выдаём всё накопленное
}

/// <summary>
/// Умный синхронизатор V3 — аккумулирует пакеты из параллельных веток,
/// обеспечивает изоляцию сессий и отправляет Success только при выполнении условия.
/// </summary>
public sealed class Logic_SynchronizerNode : BaseNode
{
    // ── Настройки ────────────────────────────────────────────────────────

    private SynchronizerMode _mode = SynchronizerMode.Accumulator;
    public SynchronizerMode Mode
    {
        get => _mode;
        set { if (_mode != value) { _mode = value; OnPropertyChanged(); } }
    }

    private int _targetCount = 2;
    /// <summary>Количество пакетов для накопления (режим Accumulator).</summary>
    public int TargetCount
    {
        get => _targetCount;
        set { if (_targetCount != value) { _targetCount = value; OnPropertyChanged(); } }
    }

    private int _timeoutSeconds = 30;
    /// <summary>Таймаут ожидания в секундах (0 = не ограничено).</summary>
    public int TimeoutSeconds
    {
        get => _timeoutSeconds;
        set { if (_timeoutSeconds != value) { _timeoutSeconds = value; OnPropertyChanged(); } }
    }

    private bool _allowCrossSession;
    /// <summary>Разрешить накопление пакетов из разных сессий.</summary>
    public bool AllowCrossSession
    {
        get => _allowCrossSession;
        set { if (_allowCrossSession != value) { _allowCrossSession = value; OnPropertyChanged(); } }
    }

    // AllowCrossSession=true отключает автоматическую проверку сессии в BaseNode.ExecuteAsync
    protected override bool AutoValidatesSession => !AllowCrossSession;

    // ── Аккумулятор (per-session, thread-safe) ────────────────────────────

    private readonly ConcurrentDictionary<Guid, List<DataBusPacket>> _accumulator = new();

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        if (inputPacket is null)
            return NodeResult.Failure("Logic_SynchronizerNode: входной пакет отсутствует.");

        // AutoValidatesSession => !AllowCrossSession — базовый Pre-flight уже вызван в ExecuteAsync

        var sessionId = inputPacket.SessionId;
        var packets   = _accumulator.GetOrAdd(sessionId, _ => []);

        lock (packets)
            packets.Add(inputPacket);

        NodeResult result;

        if (Mode == SynchronizerMode.Accumulator)
            result = await WaitForCountAsync(packets, sessionId, ct).ConfigureAwait(false);
        else
            result = await WaitForTimerAsync(packets, sessionId, ct).ConfigureAwait(false);

        return result;
    }

    private async Task<NodeResult> WaitForCountAsync(
        List<DataBusPacket> packets, Guid sessionId, CancellationToken ct)
    {
        int limit  = TimeoutSeconds > 0 ? TimeoutSeconds * 1_000 : 60_000;
        int target = Math.Max(1, TargetCount);

        using var timeoutCts = new CancellationTokenSource(limit);
        using var linked     = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        while (true)
        {
            ResetWatchdogTimer();

            int count;
            lock (packets) count = packets.Count;

            if (count >= target)
                break;

            try
            {
                await Task.Delay(200, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                int arrived;
                lock (packets) arrived = packets.Count;
                _accumulator.TryRemove(sessionId, out _);
                return NodeResult.Failure($"Синхронизация: таймаут {TimeoutSeconds}с. Накоплено {arrived}/{target} пакетов.");
            }
        }

        return BuildSuccess(packets, sessionId);
    }

    private async Task<NodeResult> WaitForTimerAsync(
        List<DataBusPacket> packets, Guid sessionId, CancellationToken ct)
    {
        int waitMs = TimeoutSeconds > 0 ? TimeoutSeconds * 1_000 : 5_000;
        int elapsed = 0;

        while (elapsed < waitMs)
        {
            ct.ThrowIfCancellationRequested();
            ResetWatchdogTimer();
            await Task.Delay(200, ct).ConfigureAwait(false);
            elapsed += 200;
        }

        return BuildSuccess(packets, sessionId);
    }

    private NodeResult BuildSuccess(List<DataBusPacket> packets, Guid sessionId)
    {
        List<DataBusPacket> snapshot;
        lock (packets)
            snapshot = [..packets];

        _accumulator.TryRemove(sessionId, out _);

        LogToBlackBox("Синхронизация успешна");

        var output = DataBusPacket.Object(sessionId, PortDataType.Object);
        DataBus?.Set(output.SessionId, output.DataId, snapshot);
        return NodeResult.Success(output);
    }
}
