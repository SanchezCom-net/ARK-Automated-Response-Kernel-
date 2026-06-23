namespace ARK.UI.Core.Bus;

public interface IDataBus
{
    void   Set(Guid sessionId, Guid dataId, object value);
    bool   TryGet(Guid sessionId, Guid dataId, out object? value);
    object? Get(Guid sessionId, Guid dataId);
    void   Remove(Guid sessionId, Guid dataId);
    void   ClearSession(Guid sessionId);
    int    SessionCount(Guid sessionId);
}
