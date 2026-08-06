namespace M3Undle.Web.Data.Entities;

/// <summary>
/// One playable episode for a series whose provider gave no stable per-series ID — i.e. a plain
/// M3U playlist, where each line is already a complete episode rather than something requiring a
/// separate get_series_info call the way Xtream does (that expanded payload is cached in
/// XtreamSeriesCache instead). Keyed by the same (ProviderGroupId, ProviderItemKey) identity as
/// the aggregate CatalogItem row for the series, so build-only can rebuild M3U series episodes
/// without a live fetch, mirroring what XtreamSeriesCache already does for Xtream series.
/// </summary>
public sealed class CatalogSeriesEpisode
{
    public string CatalogSeriesEpisodeId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderGroupId { get; set; } = string.Empty;
    public string ProviderItemKey { get; set; } = string.Empty;
    public string EpisodeKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool Active { get; set; }

    public Provider Provider { get; set; } = null!;
    public ProviderGroup ProviderGroup { get; set; } = null!;
}
