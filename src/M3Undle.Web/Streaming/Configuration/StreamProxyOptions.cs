namespace M3Undle.Web.Streaming.Configuration;

public sealed class StreamProxyOptions
{
    public bool StreamingEnabled { get; set; } = true;

    public int MaxConcurrentSessions { get; set; } = 50;

    public int? ProviderMaxConcurrentUpstreams { get; set; }

    public TimeSpan IdleGrace { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan IdleGraceHardCap { get; set; } = TimeSpan.FromSeconds(120);

    public bool EnableDetailedStreamStatus { get; set; } = true;

    public int DetailedStatusRetentionSeconds { get; set; } = 120;

    public bool EnableProviderStreamDetails { get; set; } = true;

    public bool EnableClientStreamDetails { get; set; } = true;

    public bool StatusIncludeUserAgent { get; set; } = true;
}

