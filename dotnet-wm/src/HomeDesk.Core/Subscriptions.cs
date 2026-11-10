using WinManager.Contracts;

namespace WinManager.Core;

public interface IManagerEventSink
{
    bool TryPublish(EventEnvelope envelope);
    void Disconnect(string reason);
}