namespace M3Undle.Web.Streaming.Configuration;

public sealed class ReconnectOptions
{
    public TimeSpan ReadStallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// For MPEG-TS relay sessions: how long to wait with no non-null TS content
    /// before reconnecting. Shorter than ReadStallTimeout so CDN segment-gap
    /// stalls are detected quickly without waiting the full byte-arrival timeout.
    /// </summary>
    public TimeSpan ContentStallTimeout { get; set; } = TimeSpan.FromSeconds(8);

    public TimeSpan OutageWindow { get; set; } = TimeSpan.FromSeconds(75);

    public TimeSpan StrikeCooldown { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public int[] FixedStepBackoffSeconds { get; set; } = [0, 1, 2, 5, 10, 15, 30];
}
