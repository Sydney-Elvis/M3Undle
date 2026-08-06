namespace M3Undle.Web.Data.Entities;

public sealed class CatalogItem
{
    public string CatalogItemId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderGroupId { get; set; } = string.Empty;
    public string ProviderItemKey { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? ArtworkUrl { get; set; }
    // VOD only — lets build-only rebuild the channel straight from the DB without a live fetch.
    // Series episodes come from XtreamSeriesCache (Xtream) or CatalogSeriesEpisode (M3U) instead,
    // so this stays null for series rows.
    public string? StreamUrl { get; set; }
    public int EpisodeCount { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool Active { get; set; }

    public Provider Provider { get; set; } = null!;
    public ProviderGroup ProviderGroup { get; set; } = null!;
}
