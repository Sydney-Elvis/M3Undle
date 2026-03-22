namespace M3Undle.Web.Streaming.Observability;

public sealed record StreamClientSnapshot(
    string ClientId,
    string SessionId,
    string RequestedRoute,
    string? RemoteIp,
    string? UserAgent,
    DateTimeOffset ConnectedUtc,
    long BytesSent,
    int QueueDepth);

