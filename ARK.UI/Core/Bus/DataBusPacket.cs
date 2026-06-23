using System.Collections.Frozen;

namespace ARK.UI.Core.Bus;

public enum PortDataType  { Signal, Text, Image, Object, Any, ResetRequest }
public enum PacketStatus  { Success, Error, Warning }

/// <summary>
/// V3 Идентификатор-пропуск (Passport). Тяжёлые данные хранятся строго в DataBus по ключу
/// "{SessionId}_{DataId}". Packet не несёт полезной нагрузки — только маршрутные метки.
/// </summary>
public sealed record DataBusPacket
{
    public Guid         SessionId    { get; init; } = Guid.NewGuid();
    public Guid         DataId       { get; init; } = Guid.NewGuid();
    public PortDataType Type         { get; init; } = PortDataType.Any;
    public DateTime     Timestamp    { get; init; } = DateTime.UtcNow;
    public Guid         SourceNodeId { get; init; } = Guid.Empty;
    public PacketStatus Status       { get; init; } = PacketStatus.Success;

    // Паспорт данных: типизированные теги формата {Type:Value} (например "{Sys:CPU_Load}")
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = FrozenDictionary<string, string>.Empty;

    public string CompositeKey => $"{SessionId}_{DataId}";

    // ── Фабрики: данные НЕ хранятся в пакете; вызывающий код обязан вызвать DataBus.Set() ──

    public static DataBusPacket Signal(Guid sessionId, Guid sourceNodeId = default)
        => new() { SessionId = sessionId, SourceNodeId = sourceNodeId, Type = PortDataType.Signal };

    /// <param name="meta">Необязательные теги метаданных формата {Type:Value}.</param>
    public static DataBusPacket Text(Guid sessionId, Guid sourceNodeId = default,
                                     Dictionary<string, string>? meta = null)
        => new()
        {
            SessionId    = sessionId,
            SourceNodeId = sourceNodeId,
            Type         = PortDataType.Text,
            Metadata     = meta?.ToFrozenDictionary()
                           ?? FrozenDictionary<string, string>.Empty
        };

    public static DataBusPacket Object(Guid sessionId, PortDataType type = PortDataType.Object,
                                       Guid sourceNodeId = default)
        => new() { SessionId = sessionId, SourceNodeId = sourceNodeId, Type = type };

    public static DataBusPacket Reset(Guid sessionId)
        => new() { SessionId = sessionId, Type = PortDataType.ResetRequest };

    public static DataBusPacket Failure(Guid sessionId, Guid sourceNodeId = default)
        => new()
        {
            SessionId    = sessionId,
            SourceNodeId = sourceNodeId,
            Status       = PacketStatus.Error
        };
}
