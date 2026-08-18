namespace M3Undle.Web.Contracts;

public sealed record CustomGroupDto
{
    public string CustomGroupId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Decision { get; set; } = "include";
    public string ChannelMode { get; set; } = "select";
    public string TrackingPolicy { get; set; } = "review";
    public string? TrackingKeywords { get; set; }
    public int? AutoNumStart { get; set; }
    public int? AutoNumEnd { get; set; }
    public bool TrackNewChannels { get; set; }
    public int? SortOverride { get; set; }
    public int ChannelCount { get; set; }
    public int SelectedChannelCount { get; set; }
    public int ProviderLinkCount { get; set; }
    public HashSet<string> LinkedProviderGroupIds { get; set; } = [];
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class CreateCustomGroupRequest
{
    public string Name { get; set; } = string.Empty;
}

public sealed class UpdateCustomGroupRequest
{
    public string? Name { get; set; }
    public string? Decision { get; set; }
    public string? ChannelMode { get; set; }
    public string? TrackingPolicy { get; set; }
    public string? TrackingKeywords { get; set; }
    public bool ClearTrackingKeywords { get; set; }
    public int? AutoNumStart { get; set; }
    public bool ClearAutoNum { get; set; }
    public int? AutoNumEnd { get; set; }
    public bool ClearAutoNumEnd { get; set; }
    public bool? TrackNewChannels { get; set; }
    public int? SortOverride { get; set; }
}

public sealed record CustomGroupChannelDto
{
    public string CustomGroupChannelId { get; set; } = string.Empty;
    public string ProviderChannelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? TvgId { get; set; }
    public string? GroupTitle { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public bool Active { get; set; }
    public string State { get; set; } = "included";
    public int? ChannelNumber { get; set; }
    public string? DisplayNameOverride { get; set; }
    public string? TvgIdOverride { get; set; }
}

public sealed class AddCustomGroupChannelsRequest
{
    public List<string> ProviderChannelIds { get; set; } = [];
}

public sealed class UpdateCustomGroupChannelRequest
{
    public string? State { get; set; }
    public int? ChannelNumber { get; set; }
    public bool ClearChannelNumber { get; set; }
    public string? DisplayNameOverride { get; set; }
    public bool ClearDisplayNameOverride { get; set; }
    public string? TvgIdOverride { get; set; }
    public bool ClearTvgIdOverride { get; set; }
}

public sealed class CustomGroupProviderLinkDto
{
    public string LinkId { get; set; } = string.Empty;
    public string ProviderGroupId { get; set; } = string.Empty;
    public string ProviderGroupRawName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public int? ChannelCount { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class AddCustomGroupProviderLinkRequest
{
    public string ProviderGroupId { get; set; } = string.Empty;
}

public sealed record CustomGroupChannelSearchResultDto
{
    public string ProviderChannelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? TvgId { get; set; }
    public string? GroupTitle { get; set; }
    public string ProviderGroupRawName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public bool AlreadyInGroup { get; set; }
}
