using System.Collections.Concurrent;

namespace ARK.UI.Core.Bus;

public sealed class DataBus : IDataBus
{
    // Ключ: "{SessionId}_{DataId}" — гарантирует изоляцию данных между сессиями
    private readonly ConcurrentDictionary<string, object> _store = new();

    private static string Key(Guid sessionId, Guid dataId) => $"{sessionId}_{dataId}";

    public void Set(Guid sessionId, Guid dataId, object value)
        => _store[Key(sessionId, dataId)] = value;

    public bool TryGet(Guid sessionId, Guid dataId, out object? value)
    {
        bool found = _store.TryGetValue(Key(sessionId, dataId), out var v);
        value = found ? v : null;
        return found;
    }

    public object? Get(Guid sessionId, Guid dataId)
        => _store.TryGetValue(Key(sessionId, dataId), out var v) ? v : null;

    public void Remove(Guid sessionId, Guid dataId)
        => _store.TryRemove(Key(sessionId, dataId), out _);

    public void ClearSession(Guid sessionId)
    {
        string prefix = sessionId.ToString();
        foreach (var key in _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            _store.TryRemove(key, out _);
    }

    public int SessionCount(Guid sessionId)
    {
        string prefix = sessionId.ToString();
        return _store.Keys.Count(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }
}
