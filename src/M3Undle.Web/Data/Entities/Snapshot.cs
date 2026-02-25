namespace M3Undle.Web.Data.Entities;

public sealed class Snapshot
{
    public string SnapshotId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string PlaylistPath { get; set; } = string.Empty;
    public string XmltvPath { get; set; } = string.Empty;
    public string ChannelIndexPath { get; set; } = string.Empty;
    public string StatusJsonPath { get; set; } = string.Empty;
    public int ChannelCountPublished { get; set; }
    public string? ErrorSummary { get; set; }

    public Profile Profile { get; set; } = null!;
}

