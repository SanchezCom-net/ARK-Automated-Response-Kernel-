using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Nodes;

/// <summary>
/// Точка входа графа. NodeEngine стартует именно отсюда.
/// Триггеры (HotkeyTriggerNode, SpeechTriggerNode) — спутниковые ноды, не подключаются к ней проводами.
/// Входящие соединения запрещены (нет InPort). Нода не удаляемая и не опасная.
/// ExecuteCoreAsync всегда возвращает true (pass-through к первой рабочей ноде).
/// </summary>
public sealed class TriggerRootNode : BaseNode
{
    // CardBodyWidth=140 → SuccessPortCenter.X = X+161
    // SuccessPortYCenter=27 → VerticalAlignment="Center" для карточки ~54px высотой
    [System.Text.Json.Serialization.JsonIgnore]
    public override int CardBodyWidth => 140;

    [System.Text.Json.Serialization.JsonIgnore]
    public override int SuccessPortYCenter => 27;

    [System.Text.Json.Serialization.JsonIgnore]
    public override bool IsRemovable => false;

    [System.Text.Json.Serialization.JsonIgnore]
    public override bool IsDangerous => false;

    protected override Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
        => Task.FromResult(true);
}
