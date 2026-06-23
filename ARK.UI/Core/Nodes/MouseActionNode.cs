using ARK.UI.Core.Bus;
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

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        DebugSink?.Invoke($"[МЫШЬ] Запуск. Действие: {ActionType}, координаты: ({X}, {Y})");

        bool _hasInput = false;
        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _rawC))
        {
            var _cs = _rawC as string ?? _rawC?.ToString();
            if (_cs is not null)
            {
                var _pts = _cs.Split(',', StringSplitOptions.TrimEntries);
                if (_pts.Length == 2 && int.TryParse(_pts[0], out int _px) && int.TryParse(_pts[1], out int _py))
                { X = _px; Y = _py; _hasInput = true; }
            }
        }

        if (_hasInput)
            DebugSink?.Invoke($"[ВХОД] Динамические координаты приняты: ({X}, {Y})");
        else
            DebugSink?.Invoke($"[ВХОД] Используются статические координаты: ({X}, {Y})");

        if (X == -1 && Y == -1)
        {
            DebugSink?.Invoke("[МЫШЬ] Координаты не заданы (безопасный режим) — действие пропущено.");
            await NodeLogger!.LogInfoAsync(Name, "[ВВОД] Действие мыши пропущено: координаты не заданы.")
                .ConfigureAwait(false);
            return NodeResult.Success(null);
        }

        DebugSink?.Invoke($"[МЫШЬ] Выполняю {ActionType} в ({X}, {Y})...");
        var action = NodeServices!.GetRequiredService<IActionService>();

        await (ActionType switch
        {
            MouseActionType.LeftClick       => action.ClickAsync(X, Y, ct),
            MouseActionType.RightClick      => action.RightClickAsync(X, Y, ct),
            MouseActionType.DoubleLeftClick => action.DoubleClickAsync(X, Y, ct),
            MouseActionType.Move            => action.MoveAsync(X, Y, ct),
            MouseActionType.Scroll          => action.ScrollAsync(X, Y, ScrollAmount, ct),
            MouseActionType.LeftButtonDown  => action.MouseButtonDownAsync(X, Y, ct),
            MouseActionType.LeftButtonUp    => action.MouseButtonUpAsync(X, Y, ct),
            _                               => Task.CompletedTask
        }).ConfigureAwait(false);

        string _coords = $"{X},{Y}";
        LastOutputValue = new DataPacket { Type = DataType.Text, Payload = _coords };
        DebugSink?.Invoke($"[ВЫХОД] DataBus записан: «{_coords}»");

        await NodeLogger!.LogInfoAsync(Name,
            $"[ВВОД] Симуляция мыши завершена: {ActionType} в ({X}, {Y}).")
            .ConfigureAwait(false);

        var _sid = inputPacket?.SessionId ?? Guid.NewGuid();
        var _out = DataBusPacket.Text(_sid);
        DataBus?.Set(_out.SessionId, _out.DataId, _coords);
        return NodeResult.Success(_out);
    }
}
