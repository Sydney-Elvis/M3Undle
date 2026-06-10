namespace M3Undle.Web.Data.Entities;

public sealed class XtreamSeriesCache
{
    public string ProviderId { get; set; } = string.Empty;
    public int SeriesId { get; set; }
    public long LastModifiedEpoch { get; set; }
    public string EpisodesJson { get; set; } = string.Empty;

    public Provider Provider { get; set; } = null!;
}
