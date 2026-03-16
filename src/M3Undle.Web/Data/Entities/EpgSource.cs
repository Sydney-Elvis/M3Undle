namespace M3Undle.Web.Data.Entities;

public sealed class EpgSource
{
    public string EpgSourceId { get; set; } = string.Empty;
    public string? ProviderId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>provider_xmltv | xmltv_url | xmltv_file</summary>
    public string Kind { get; set; } = "xmltv_url";

    public string? UrlOrPath { get; set; }
    public int Priority { get; set; } = 10;
    public bool Enabled { get; set; } = true;
    public string? HeadersJson { get; set; }
    public string? UserAgent { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public string? ETag { get; set; }
    public DateTime? LastModifiedUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastFailureUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public Provider? Provider { get; set; }
    public ICollection<EpgSourceChannel> Channels { get; set; } = new List<EpgSourceChannel>();
    public ICollection<EpgChannelMapping> ChannelMappings { get; set; } = new List<EpgChannelMapping>();
    public ICollection<EpgFetchRun> FetchRuns { get; set; } = new List<EpgFetchRun>();
}
