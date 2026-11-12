using HomeDesk.Contracts;

namespace HomeDesk.Core;

public interface IManagerEventSink
{
    bool TryPublish(EventEnvelope envelope);
    void Disconnect(string reason);
}