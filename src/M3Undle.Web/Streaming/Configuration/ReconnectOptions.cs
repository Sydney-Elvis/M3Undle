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

    /// <summary>
    /// Maximum fallback cooldown when the provider does not send Retry-After.
    /// Provider-supplied Retry-After values are honored separately.
    /// </summary>
    public TimeSpan StrikeCooldown { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ProxyAuthFallbackCooldown { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RateLimitFallbackCooldown { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan UpstreamServerErrorFallbackCooldown { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan TransportFallbackCooldown { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan RecoveryOutputHoldLimit { get; set; } = TimeSpan.FromSeconds(3);

    public int RecoverySafeStartSearchLimitBytes { get; set; } = 512 * 1024;

    public bool AllowPacketBoundaryRecoveryFallback { get; set; } = true;

    public int[] FixedStepBackoffSeconds { get; set; } = [0, 1, 2, 5, 10, 15, 30];
}
