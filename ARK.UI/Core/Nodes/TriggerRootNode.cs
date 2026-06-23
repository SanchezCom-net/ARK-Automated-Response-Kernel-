using ARK.UI.Core.Bus;

namespace ARK.UI.Core.Nodes;

/// <summary>
/// Нода СТАРТ — Якорь Регистрации Триггеров (V3 стандарт).
///
/// Роль: «Рубильник питания» для макроса. Является исключительно Compile-time артефактом:
/// компилятор графа (EventMonitor.RefreshTriggersCacheAsync) обходит провода из этой ноды
/// и регистрирует только подключённые триггерные ноды. Любые триггеры, не подключённые
/// к TriggerRootNode, считаются «Мёртвым кодом» (Dead Code) и в кэш не попадают.
///
/// В Runtime нода не производит никаких действий — мгновенно передаёт управление.
///
/// Порты:
///   Входы : НЕТ (нет Trigger In, нет Data In)
///   Выходы: Registration Link (= Success Trigger) — один провод к активному триггеру
/// </summary>
public sealed class TriggerRootNode : BaseNode
{
    // V3: IsDeletable = false — нода СТАРТ не может быть удалена с холста
    [System.Text.Json.Serialization.JsonIgnore]
    public override bool IsRemovable => false;

    [System.Text.Json.Serialization.JsonIgnore]
    public override bool IsDangerous => false;

    // Кнопки тестирования не применимы — нода является compile-time якорем
    [System.Text.Json.Serialization.JsonIgnore]
    public override bool IsTestable => false;

    // Карточка без портов ввода — только Registration Link (Success Out)
    // CardBodyWidth=140 должен точно совпадать с ColumnDefinition Width="140" в NodeCardTriggerRootTemplate
    [System.Text.Json.Serialization.JsonIgnore]
    public override int CardBodyWidth => 140;

    [System.Text.Json.Serialization.JsonIgnore]
    public override int SuccessPortYCenter => 27;

    // ── Self-Collision Policy ────────────────────────────────────────────────

    /// <summary>Все допустимые стратегии коллизии — источник для ComboBox в редакторе.</summary>
    public static readonly SelfCollisionStrategy[] AllCollisionStrategies
        = Enum.GetValues<SelfCollisionStrategy>();

    private SelfCollisionStrategy _collisionStrategy = SelfCollisionStrategy.Parallel;

    /// <summary>
    /// Поведение макроса при получении нового триггера, пока предыдущее выполнение активно.
    /// Сериализуется в JSON макроса; читается MacroOrchestrator перед запуском.
    /// </summary>
    public SelfCollisionStrategy CollisionStrategy
    {
        get => _collisionStrategy;
        set { if (_collisionStrategy == value) return; _collisionStrategy = value; OnPropertyChanged(); }
    }

    // ── Runtime ──────────────────────────────────────────────────────────────

    // В Runtime — мгновенная передача управления. Вся смысловая нагрузка на стороне EventMonitor.
    protected override Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
        => Task.FromResult(NodeResult.Success(null));
}
