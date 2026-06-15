namespace M3Undle.Web.Streaming.Configuration;

public sealed class ReconnectOptions
{
    public TimeSpan ReadStallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// For direct MPEG-TS relay sessions: how long to wait with no non-null TS
    /// content before reconnecting. Shorter than ReadStallTimeout so CDN
    /// segment-gap stalls are detected quickly without waiting the full
    /// byte-arrival timeout. Kept low because a direct stall means M3Undle itself
    /// must reconnect — the sooner detected, the shorter the on-screen freeze.
    /// </summary>
    public TimeSpan ContentStallTimeout { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// For FFmpeg relay sessions (clean remux / HLS): how long to wait with no
    /// output before M3Undle tears the FFmpeg process down and reconnects the
    /// whole session. Deliberately longer than <see cref="ContentStallTimeout"/>
    /// because FFmpeg reconnects to the provider internally; tearing it down too
    /// eagerly would fight its own recovery and cause more disruption than the
    /// blip it is riding out.
    /// </summary>
    public TimeSpan FfmpegRelayStallTimeout { get; set; } = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Only client aborts that occur within this window of a recovery resume are
    /// counted as <c>ClientAbortAfterRecovery</c> health evidence. Aborts later
    /// than this (channel changes, idle closes, unrelated disconnects minutes
    /// after a clean recovery) are ordinary disconnects and must not poison the
    /// channel health profile. See issue #96.
    /// </summary>
    public TimeSpan PostRecoveryAbortWindow { get; set; } = TimeSpan.FromSeconds(15);

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
