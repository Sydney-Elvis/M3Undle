namespace M3Undle.Web.Streaming.Observability;

public enum ClientHealthStatus
{
    Unknown,
    Live,
    Slow,
    Stalled,
}

public static class StreamClientHealth
{
    public static ClientHealthStatus Evaluate(
        StreamClientSnapshot client,
        long? sessionUpstreamBytesPerSecond,
        int hlsSegmentDurationSeconds,
        int hlsInactivityTimeoutSeconds,
        DateTimeOffset nowUtc)
    {
        if (client.Transport == ClientTransport.GeneratedHls)
        {
            var lastAccess = client.LastActivityUtc ?? client.ConnectedUtc;
            if (nowUtc - lastAccess >= TimeSpan.FromSeconds(hlsInactivityTimeoutSeconds))
                return ClientHealthStatus.Stalled;

            var healthyWindow = TimeSpan.FromSeconds(Math.Max(6, hlsSegmentDurationSeconds * 3));
            if (client.LastMediaActivityUtc is null)
                return nowUtc - client.ConnectedUtc <= healthyWindow
                    ? ClientHealthStatus.Unknown
                    : ClientHealthStatus.Slow;

            return nowUtc - client.LastMediaActivityUtc.Value <= healthyWindow
                ? ClientHealthStatus.Live
                : ClientHealthStatus.Slow;
        }

        if (client.QueueDepth > 20 && (client.BytesPerSecond is null or 0))
            return ClientHealthStatus.Stalled;

        var isSlow = client.BytesPerSecond is > 0
            && sessionUpstreamBytesPerSecond is > 0
            && client.BytesPerSecond.Value < sessionUpstreamBytesPerSecond.Value / 4
            && client.QueueDepth > 2;

        return isSlow ? ClientHealthStatus.Slow : ClientHealthStatus.Live;
    }
}
