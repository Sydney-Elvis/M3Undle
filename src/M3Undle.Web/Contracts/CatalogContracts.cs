namespace M3Undle.Web.Contracts;

public sealed class CatalogGroupDto
{
    public string ProviderGroupId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public int TitleCount { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool ProviderEnabled { get; set; }
    public bool ProfileProviderEnabled { get; set; }
    public bool ContentTypeEnabled { get; set; }
    public string Decision { get; set; } = "include";
    public bool IsNew { get; set; }
}

public sealed class UpdateCatalogGroupDecisionRequest
{
    public string Decision { get; set; } = string.Empty;
}

public sealed class CatalogItemDto
{
    public string CatalogItemId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public int EpisodeCount { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public string ProviderGroupId { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
}

public sealed class CatalogItemsResponse
{
    public string ProviderGroupId { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<CatalogItemDto> Items { get; set; } = [];
}

public sealed class CatalogTitleSearchResponse
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<CatalogItemDto> Items { get; set; } = [];
}

public sealed class CatalogItemDetailDto
{
    public string CatalogItemId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string? Plot { get; set; }
    public string? Genre { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Director { get; set; }
    public string? Cast { get; set; }
    public string? Rating { get; set; }
    public string? Duration { get; set; }
    public bool HasArtwork { get; set; }
    public string? MetadataNotice { get; set; }
    public List<CatalogSeasonDto> Seasons { get; set; } = [];
}

public sealed class CatalogSeasonDto
{
    public int SeasonNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<CatalogEpisodeDto> Episodes { get; set; } = [];
}

public sealed class CatalogEpisodeDto
{
    public int EpisodeNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Plot { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Duration { get; set; }
}
