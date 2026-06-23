using ARK.UI.Core.Bus;

namespace ARK.UI.Core.Nodes;

public enum ConflictStrategy
{
    Yield,      // Ждать завершения текущего экземпляра
    Interrupt,  // Прервать текущий и запустить новый
    Parallel    // Запускать параллельно без ограничений
}

/// <summary>
/// Passive Configurator — задаёт политику поведения макроса.
/// Не имеет входных/выходных портов; не вызывается движком напрямую.
/// Параметры читаются MacroScheduler при загрузке макроса.
/// </summary>
public sealed class MacroPolicyNode : BaseNode
{
    // ── Флаг пассивной ноды: движок и UI прячут порты ───────────────────
    [System.Text.Json.Serialization.JsonIgnore]
    public override bool IsPassive => true;

    [System.Text.Json.Serialization.JsonIgnore]
    public override bool IsRemovable => true;

    // Пассивная нода-конфигуратор: кнопки тестирования не применимы
    [System.Text.Json.Serialization.JsonIgnore]
    public override bool IsTestable => false;

    // ── Политики ─────────────────────────────────────────────────────────

    private bool _systemImmunity;
    /// <summary>Макрос не может быть прерван другими макросами и системными событиями.</summary>
    public bool SystemImmunity
    {
        get => _systemImmunity;
        set { if (_systemImmunity != value) { _systemImmunity = value; OnPropertyChanged(); } }
    }

    private bool _monopolyMode;
    /// <summary>Запрещает одновременный запуск любых других макросов пока этот активен.</summary>
    public bool MonopolyMode
    {
        get => _monopolyMode;
        set { if (_monopolyMode != value) { _monopolyMode = value; OnPropertyChanged(); } }
    }

    private int _priorityLevel;
    /// <summary>Приоритет планировщика (0 — по умолчанию, 1–999 — строгая очередь).</summary>
    public int PriorityLevel
    {
        get => _priorityLevel;
        set { if (_priorityLevel != value) { _priorityLevel = value; OnPropertyChanged(); } }
    }

    private ConflictStrategy _strategy = ConflictStrategy.Parallel;
    /// <summary>Стратегия поведения при конфликте (одновременный повторный запуск).</summary>
    public ConflictStrategy Strategy
    {
        get => _strategy;
        set { if (_strategy != value) { _strategy = value; OnPropertyChanged(); } }
    }

    // Пассивная нода не должна вызываться движком напрямую, но обязана реализовать абстракт.
    protected override Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
        => Task.FromResult(NodeResult.Success(null));
}
