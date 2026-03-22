namespace M3Undle.Web.Data.Entities;

public sealed class EpgChannelMapping
{
    public string EpgChannelMappingId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string ProviderChannelId { get; set; } = string.Empty;
    public string EpgSourceId { get; set; } = string.Empty;
    public string XmltvChannelId { get; set; } = string.Empty;

    /// <summary>auto_id | auto_name | auto_fuzzy | manual</summary>
    public string MappingMode { get; set; } = "auto_id";

    public float Confidence { get; set; } = 1.0f;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public Profile Profile { get; set; } = null!;
    public ProviderChannel ProviderChannel { get; set; } = null!;
    public EpgSource EpgSource { get; set; } = null!;
}
