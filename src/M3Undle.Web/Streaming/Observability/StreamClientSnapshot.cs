namespace M3Undle.Web.Streaming.Observability;

public enum ClientTransport
{
    DirectRelay,
    GeneratedHls,
}

/// <summary>
/// How a stream is delivered to an individual client, independent of how M3Undle ingests
/// it from the provider (the per-stream relay mode). One ingest can fan out to several
/// delivery methods across mixed clients on the same channel.
/// </summary>
public enum DeliveryMethod
{
    /// <summary>Direct MPEG-TS passthrough over a long-lived HTTP response.</summary>
    RawTs,

    /// <summary>FFmpeg-remuxed MPEG-TS (clean PAT/PMT, PCR/PTS, continuity) over HTTP.</summary>
    RemuxTs,

    /// <summary>Segmented HLS that the client pulls at its own pace.</summary>
    Hls,
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
    long? BytesPerSecond = null,
    DeliveryMethod Delivery = DeliveryMethod.RawTs,
    string? DeliveryReason = null);

