namespace M3Undle.Web.Data.Entities;

public sealed class FetchRun
{
    public string FetchRunId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Type { get; set; } = "snapshot";
    public string? ErrorSummary { get; set; }
    public string? PlaylistEtag { get; set; }
    public string? PlaylistLastModified { get; set; }
    public string? XmltvEtag { get; set; }
    public string? XmltvLastModified { get; set; }
    public int? PlaylistBytes { get; set; }
    public int? XmltvBytes { get; set; }
    public int? ChannelCountSeen { get; set; }

    public Provider Provider { get; set; } = null!;
    public ICollection<ProviderChannel> ProviderChannels { get; set; } = new List<ProviderChannel>();
}

