namespace M3Undle.Web.Streaming.Observability;

public enum ClientTransport
{
    DirectRelay,
    GeneratedHls,
}

public sealed record StreamClientSnapshot(
    string ClientId,
    string SessionId,
    string RequestedRoute,
    string? RemoteIp,
    string? UserAgent,
    DateTimeOffset ConnectedUtc,
    long BytesSent,
    int QueueDepth,
    bool IsInternal = false,
    ClientTransport Transport = ClientTransport.DirectRelay,
    long? BytesPerSecond = null);

