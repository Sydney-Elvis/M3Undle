namespace M3Undle.Web.Application;

public sealed class RefreshOptions
{
    public int IntervalHours { get; set; } = 4;
    public int TimeoutMinutes { get; set; } = 30;
    public int StartupDelaySeconds { get; set; } = 30;

    /// <summary>
    /// How long an inactive provider channel with no user mappings is kept before it is purged.
    /// Mapped channels are never purged regardless of this value.
    /// </summary>
    public int ChannelRetentionDays { get; set; } = 30;
}

public sealed class SnapshotOptions
{
    public int RetentionCount { get; set; } = 3;

    /// <summary>
    /// Besides the newest <see cref="RetentionCount"/> snapshots, the newest snapshot at least this many
    /// hours old is always kept. 0 disables it.
    /// </summary>
    public int SafetySnapshotAgeHours { get; set; } = 24;

    /// <summary>
    /// A fetch whose live channel count falls below this fraction of what is currently active is held
    /// instead of applied (an empty fetch is always held). 0.5 means "lost more than half".
    /// </summary>
    public double SuspectFetchMinRetainedRatio { get; set; } = 0.5;

    /// <summary>
    /// A held fetch is accepted as a real change once this many consecutive refreshes return the same
    /// reduced lineup. 0 never accepts automatically.
    /// </summary>
    public int SuspectFetchAcceptAfterRuns { get; set; } = 3;
    public string Directory { get; set; } = "snapshots";
}

