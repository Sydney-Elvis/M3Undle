namespace M3Undle.Web.Application;

public sealed class HdHomeRunOptions
{
    public bool Enabled { get; set; } = true;
    public bool DiscoveryEnabled { get; set; }
    public bool SsdpEnabled { get; set; } = true;
    public bool SiliconDustDiscoveryEnabled { get; set; } = true;
    public int TunerCount { get; set; } = 1;
    public string FriendlyName { get; set; } = "M3Undle HDHomeRun";
    public string ModelNumber { get; set; } = "HDHR3-US";
    public string FirmwareName { get; set; } = "hdhomerun_atsc";
    public string FirmwareVersion { get; set; } = "20260312";
    public string Manufacturer { get; set; } = "Silicondust";
    public string? AdvertisedBaseUrl { get; set; }
}

