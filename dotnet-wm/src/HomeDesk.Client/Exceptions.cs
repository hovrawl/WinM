namespace WinManager.Client;

public sealed class WinManagerRemoteException(string errorCode, string message, long stateRevision) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
    public long StateRevision { get; } = stateRevision;
}

public sealed class WinManagerProtocolException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class WinManagerConsumerLagException(int capacity)
    : Exception($"The event consumer exceeded its queue capacity of {capacity}. The connection was closed; reconnect for a fresh snapshot.")
{
    public int Capacity { get; } = capacity;
}