using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ARK.UI.Core.Nodes;

public enum MouseActionType
{
    LeftClick,
    RightClick,
    DoubleLeftClick,
    Move,
    Scroll,
    LeftButtonDown,
    LeftButtonUp,
}

public sealed class MouseActionNode : BaseNode
{
    public override string DefaultDataInputPropertyName => "Coordinates";

    public static readonly MouseActionType[] AllActionTypes = Enum.GetValues<MouseActionType>();

    private MouseActionType _actionType = MouseActionType.LeftClick;
    public MouseActionType ActionType
    {
        get => _actionType;
        set { if (_actionType != value) { _actionType = value; OnPropertyChanged(); } }
    }

    private int _x = -1;
    public int X
    {
        get => _x;
        set { if (_x != value) { _x = value; OnPropertyChanged(); } }
    }

    private int _y = -1;
    public int Y
    {
        get => _y;
        set { if (_y != value) { _y = value; OnPropertyChanged(); } }
    }

    private int _scrollAmount = 120;
    public int ScrollAmount
    {
        get => _scrollAmount;
        set { if (_scrollAmount != value) { _scrollAmount = value; OnPropertyChanged(); } }
    }

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider,
        ILogService logger,
        CancellationToken cancellationToken)
    {
        DebugSink?.Invoke($"[МЫШЬ] Запуск. Действие: {ActionType}, координаты: ({X}, {Y})");

        // ── Input: полная строка координат "X,Y" по серебряному проводу ───
        bool hasCoord = TryApplyContextInput<string>("Coordinates", v =>
        {
            var p = v.Split(',', StringSplitOptions.TrimEntries);
            if (p.Length == 2 && int.TryParse(p[0], out int px) && int.TryParse(p[1], out int py))
            { X = px; Y = py; }
        });
        // Fallback: отдельные целочисленные входы
        bool hasX = TryApplyContextInput<int>("X", v => X = v);
        bool hasY = TryApplyContextInput<int>("Y", v => Y = v);

        if (hasCoord || hasX || hasY)
            DebugSink?.Invoke($"[ВХОД] Динамические координаты приняты: ({X}, {Y})");
        else
            DebugSink?.Invoke($"[ВХОД] Используются статические координаты: ({X}, {Y})");

        // ── Защитный барьер от ложных кликов при инициализации ноды ──────
        if (X == -1 && Y == -1)
        {
            DebugSink?.Invoke("[МЫШЬ] Координаты не заданы (безопасный режим) — действие пропущено.");
            await logger.LogInfoAsync(Name,
                "[ВВОД] Действие мыши пропущено: координаты не заданы (безопасный режим).")
                .ConfigureAwait(false);
            return true;
        }

        DebugSink?.Invoke($"[МЫШЬ] Выполняю {ActionType} в ({X}, {Y})...");
        var action = serviceProvider.GetRequiredService<IActionService>();

        await (ActionType switch
        {
            MouseActionType.LeftClick       => action.ClickAsync(X, Y, cancellationToken),
            MouseActionType.RightClick      => action.RightClickAsync(X, Y, cancellationToken),
            MouseActionType.DoubleLeftClick => action.DoubleClickAsync(X, Y, cancellationToken),
            MouseActionType.Move            => action.MoveAsync(X, Y, cancellationToken),
            MouseActionType.Scroll          => action.ScrollAsync(X, Y, ScrollAmount, cancellationToken),
            MouseActionType.LeftButtonDown  => action.MouseButtonDownAsync(X, Y, cancellationToken),
            MouseActionType.LeftButtonUp    => action.MouseButtonUpAsync(X, Y, cancellationToken),
            _                               => Task.CompletedTask
        }).ConfigureAwait(false);

        DebugSink?.Invoke($"[МЫШЬ] {ActionType} в ({X}, {Y}) выполнен ✓");

        // ── Output: координаты в формате "X,Y" для цепочки нод ───────────
        LastOutputValue = new DataPacket { Type = DataType.Text, Payload = $"{X},{Y}" };
        DebugSink?.Invoke($"[ВЫХОД] DataPacket записан: «{X},{Y}»");

        await logger.LogInfoAsync(Name,
            $"[ВВОД] Симуляция мыши завершена: {ActionType} в координаты ({X}, {Y}).")
            .ConfigureAwait(false);

        return true;
    }
}
