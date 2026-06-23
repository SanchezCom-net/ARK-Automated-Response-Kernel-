namespace ARK.UI.Core.Bus;

public sealed class SessionMismatchException : Exception
{
    public Guid Expected { get; }
    public Guid Received { get; }

    public SessionMismatchException(Guid expected, Guid received)
        : base($"Session mismatch: expected {expected}, received {received}.")
    {
        Expected = expected;
        Received = received;
    }
}
