namespace M3Undle.Web.Data.Entities;

public sealed class SiteSettings
{
    public int Id { get; set; }
    public bool AuthenticationEnabled { get; set; }
    public bool EndpointSecurityEnabled { get; set; }
    public bool StreamingEnabled { get; set; } = true;
    public int StreamMaxConcurrentSessions { get; set; } = 50;
    public int StreamIdleGraceSeconds { get; set; } = 15;
    public int StreamIdleGraceHardCapSeconds { get; set; } = 120;
    public int StreamBufferMaxBytesPerSession { get; set; } = 4 * 1024 * 1024;
    public int StreamBufferMaxBytesHardCap { get; set; } = 32 * 1024 * 1024;
    public int StreamBufferReadChunkSizeBytes { get; set; } = 32 * 1024;
    public int StreamReconnectReadStallTimeoutSeconds { get; set; } = 30;
    public int StreamReconnectOutageWindowSeconds { get; set; } = 75;
    public int StreamReconnectConnectTimeoutSeconds { get; set; } = 15;
    public bool StreamingSettingsRestartRequired { get; set; }
    public bool HdhrEnabled { get; set; } = true;
    public int? HdhrTunerCountOverride { get; set; }
    public string? HdhrAdvertisedBaseUrl { get; set; }
    public bool HdhrDiscoveryEnabled { get; set; } = true;
    public bool HdhrSsdpEnabled { get; set; } = true;
    public bool HdhrSiliconDustDiscoveryEnabled { get; set; } = true;
    public bool HdhrSettingsRestartRequired { get; set; }
}
