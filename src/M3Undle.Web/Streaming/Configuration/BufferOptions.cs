namespace M3Undle.Web.Streaming.Configuration;

public sealed class BufferOptions
{
    public int MaxBytesPerSession { get; set; } = 4 * 1024 * 1024;

    public int MaxBytesHardCap { get; set; } = 32 * 1024 * 1024;

    public int ReadChunkSizeBytes { get; set; } = 32 * 1024;

    public int SubscriberQueueCapacity { get; set; } = 512;

    /// <summary>
    /// How long a subscriber's outbound queue may stay continuously full before the
    /// subscriber is evicted as a slow client. Within this window overflowing live
    /// chunks are dropped but the subscriber stays connected, which tolerates brief
    /// reader pauses (player startup buffering, momentary socket stalls) instead of
    /// disconnecting on the first overflow.
    /// </summary>
    // Bounded slow-client grace (research hard-cap neighborhood for direct TS). A genuinely
    // stuck client is evicted within this window rather than pinning memory indefinitely.
    // The proper fix for burst-buffering clients is HLS / boundary resync, not a longer grace.
    public TimeSpan SlowClientGracePeriod { get; set; } = TimeSpan.FromSeconds(10);
}
