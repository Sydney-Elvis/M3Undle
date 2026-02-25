namespace M3Undle.Web.Application;

public sealed class RefreshOptions
{
    public int IntervalHours { get; set; } = 4;
    public int TimeoutMinutes { get; set; } = 5;
    public int StartupDelaySeconds { get; set; } = 30;
}

public sealed class SnapshotOptions
{
    public int RetentionCount { get; set; } = 3;
}

