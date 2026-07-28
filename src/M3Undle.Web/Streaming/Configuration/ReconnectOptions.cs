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

    /// <summary>
    /// When a reconnected upstream replays content from before the failure point
    /// (providers commonly serve ~60-70 s of ring buffer on a fresh connection),
    /// keep holding output through the replayed span and resume at the first IDR
    /// at/after the last video DTS relayed before the failure. The replay then
    /// backfills the outage instead of flooding downstream with stale content.
    /// </summary>
    public bool EnableRecoveryOverlapTrim { get; set; } = true;

    /// <summary>
    /// Wall-clock budget for an active overlap trim. Rewind bursts arrive far faster
    /// than real time; a provider replaying at 1x would never catch up, so on expiry
    /// the trim is abandoned and recovery falls back to the standard first-IDR resume.
    /// </summary>
    public TimeSpan RecoveryOverlapTrimHoldLimit { get; set; } = TimeSpan.FromSeconds(6);

    public int RecoveryOverlapTrimMaxBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Rewinds larger than this are treated as an unrelated timestamp timeline
    /// (e.g. the provider restarted its encoder) rather than a replay to trim.
    /// </summary>
    public int RecoveryOverlapTrimMaxRewindSeconds { get; set; } = 180;

    /// <summary>
    /// After a trim is abandoned, no new trim is armed for this long. A source that
    /// fails faster than its replay can catch up (FFmpeg relay restarts produce a
    /// rewound-looking timeline on every reconnect) would otherwise re-enter a fresh
    /// trim with a fresh budget on every failure, suppressing output indefinitely;
    /// the cooldown degrades such sources to the plain first-IDR resume instead.
    /// Zero disables the cooldown.
    /// </summary>
    public TimeSpan RecoveryOverlapTrimRetryCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// For clean-remux FFmpeg relay: a real backward DTS jump never reaches the
    /// scanner, because FFmpeg's mpegts muxer enforces non-decreasing output DTS at
    /// the container level and silently clamps a provider's restart-from-zero up to
    /// last+1 each time instead. A consecutive-tick delta at or below this threshold
    /// is treated as that clamping in progress rather than genuine frame timing.
    /// Default (180 ticks @ 90kHz, about 2ms, "faster than 500fps") sits far above the
    /// observed 1-tick clamp signature and far below any plausible real frame
    /// spacing, so legitimate high-frame-rate or tightly B-frame-reordered content
    /// should not trip it.
    /// </summary>
    public long ClampedDtsRampMaxDeltaTicks { get; set; } = 180;

    /// <summary>
    /// Minimum accumulated evidence (batch-boundary crossings and within-batch spans
    /// at or below <see cref="ClampedDtsRampMaxDeltaTicks"/>) before a clamped DTS
    /// ramp is treated as a detected in-process rewind. Guards against a single
    /// noisy sample triggering recovery.
    /// </summary>
    public int ClampedDtsRampMinEvidence { get; set; } = 3;

    /// <summary>
    /// Wall-clock budget for an active clamped-DTS-ramp recovery hold. This wait is
    /// evidence-based (<see cref="ClampedDtsRampMinEvidence"/> consecutive healthy
    /// deltas), not time-based, so it needs its own cap distinct from
    /// <see cref="RecoveryOutputHoldLimit"/>: a ramp that never accumulates enough
    /// healthy crossings must not hold output forever, but legitimately slow-to-settle
    /// ramps can easily outlast the generic hold limit. On expiry the hold is
    /// abandoned and recovery falls back to the standard first-IDR resume instead of
    /// forcing a retune, mirroring how <see cref="RecoveryOverlapTrimHoldLimit"/>
    /// polices the overlap-trim path.
    /// </summary>
    public TimeSpan ClampedDtsRampHoldLimit { get; set; } = TimeSpan.FromSeconds(6);

    public int[] FixedStepBackoffSeconds { get; set; } = [0, 1, 2, 5, 10, 15, 30];
}
