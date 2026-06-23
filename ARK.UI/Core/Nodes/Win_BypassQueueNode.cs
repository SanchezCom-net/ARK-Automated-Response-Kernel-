using ARK.UI.Core.Bus;

namespace ARK.UI.Core.Nodes;

/// <summary>
/// Нода приоритетного запуска (Bypass Queue).
///
/// Управляет уровнем приоритета макроса в системе планировщика:
///   • IgnoreAllRestrictions = true  → System Level: запускается мгновенно, обходит всё
///     (очереди, монополию, условия среды). Не может быть остановлен блокировкой.
///   • BlockOthersOnExecution = true → Exclusive: монополист. Ждёт, пока нет System Level
///     или другого Exclusive, затем блокирует старт всех остальных до завершения.
///
/// Реальная логика приоритетного стека реализована в MacroScheduler.
/// </summary>
public sealed class Win_BypassQueueNode : BaseNode
{
    private bool _ignoreAllRestrictions;
    private bool _blockOthersOnExecution;

    /// <summary>System Level: игнорировать всё и запустить мгновенно.</summary>
    public bool IgnoreAllRestrictions
    {
        get => _ignoreAllRestrictions;
        set
        {
            if (_ignoreAllRestrictions == value) return;
            _ignoreAllRestrictions = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Exclusive: блокировать запуск любых других макросов на время выполнения.</summary>
    public bool BlockOthersOnExecution
    {
        get => _blockOthersOnExecution;
        set
        {
            if (_blockOthersOnExecution == value) return;
            _blockOthersOnExecution = value;
            OnPropertyChanged();
        }
    }

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        var level = IgnoreAllRestrictions ? "SYSTEM LEVEL" : "EXCLUSIVE";
        var block = BlockOthersOnExecution ? " | блокировка активна" : string.Empty;

        await NodeLogger!.LogInfoAsync(nameof(Win_BypassQueueNode),
            $"[{level}{block}] Макрос '{Name}' выполняется в приоритетном режиме.")
            .ConfigureAwait(false);

        return NodeResult.Success(null);
    }
}
