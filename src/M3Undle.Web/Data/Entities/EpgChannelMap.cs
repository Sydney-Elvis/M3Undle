namespace M3Undle.Web.Data.Entities;

public sealed class EpgChannelMap
{
    public string EpgMapId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string XmltvChannelId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public Profile Profile { get; set; } = null!;
    public CanonicalChannel Channel { get; set; } = null!;
}

