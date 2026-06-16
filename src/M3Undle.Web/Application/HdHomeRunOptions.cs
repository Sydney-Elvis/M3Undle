namespace M3Undle.Web.Application;

public sealed class HdHomeRunOptions
{
    public bool Enabled { get; set; } = true;
    public bool DiscoveryEnabled { get; set; }
    public bool SsdpEnabled { get; set; } = true;
    public bool SiliconDustDiscoveryEnabled { get; set; } = true;
    // Virtual tuner count advertised to HDHR clients when no stream limit is enforced
    // (no provider MaxConcurrentStreams and no UI tuner-count override). This is purely the
    // advertised count, not a server-side cap — clients allocate against it and typically
    // reserve one tuner for EPG/PSIP scanning, so it must leave headroom above 1. When a
    // limit IS enforced, the advertised count equals that limit instead (see HdHomeRunTunerCountResolver).
    public int TunerCount { get; set; } = 6;
    public string FriendlyName { get; set; } = "M3Undle HDHomeRun";
    public string ModelNumber { get; set; } = "HDHR3-US";
    public string FirmwareName { get; set; } = "hdhomerun_atsc";
    public string FirmwareVersion { get; set; } = "20260312";
    public string Manufacturer { get; set; } = "Silicondust";
    public string? AdvertisedBaseUrl { get; set; }
}

