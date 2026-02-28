namespace M3Undle.Web.Contracts;

public sealed class GroupFilterDto
{
    public string ProfileGroupFilterId { get; set; } = string.Empty;
    public string ProviderGroupId { get; set; } = string.Empty;
    public string ProviderGroupRawName { get; set; } = string.Empty;
    public bool ProviderGroupActive { get; set; }
    public DateTime ProviderGroupFirstSeen { get; set; }
    public DateTime ProviderGroupLastSeen { get; set; }
    public int? ChannelCount { get; set; }
    public string Decision { get; set; } = "pending";
    public string ChannelMode { get; set; } = "all";
    public string? OutputName { get; set; }
    public int? AutoNumStart { get; set; }
    public int? AutoNumEnd { get; set; }
    public bool TrackNewChannels { get; set; }
    public int? SortOverride { get; set; }
}

public sealed class UpdateGroupFilterRequest
{
    public string? Decision { get; set; }
    public string? OutputName { get; set; }
    public bool ClearOutputName { get; set; }
    public int? AutoNumStart { get; set; }
    public bool ClearAutoNum { get; set; }
    public int? AutoNumEnd { get; set; }
    public bool ClearAutoNumEnd { get; set; }
    public bool? TrackNewChannels { get; set; }
    public int? SortOverride { get; set; }
}

public sealed class BulkGroupDecisionRequest
{
    public List<string> ProviderGroupIds { get; set; } = [];
    public string Decision { get; set; } = string.Empty;
}

public sealed class ChannelMappingStatsDto
{
    public string? ProfileId { get; set; }
    public int GroupsIncluded { get; set; }
    public int GroupsPending { get; set; }
    public int ChannelsInOutput { get; set; }
    public int? ChannelsInProvider { get; set; }
}

public sealed class ActiveProfileDto
{
    public string ProfileId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class GroupChannelDto
{
    public int? Number { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? TvgId { get; set; }
}

public sealed class GroupChannelsResponse
{
    public bool IsInOutput { get; set; }
    public List<GroupChannelDto> Channels { get; set; } = [];
}

public sealed class ProviderChannelSelectDto
{
    public string ProviderChannelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? TvgId { get; set; }
    public bool Active { get; set; }
    public bool IsSelected { get; set; }
    public string? OutputGroupName { get; set; }
    public int? ChannelNumber { get; set; }
}

public sealed class ChannelSelectionsDto
{
    public string ChannelMode { get; set; } = "all";
    public List<ProviderChannelSelectDto> Channels { get; set; } = [];
}

public sealed class ChannelSelectionItem
{
    public string ProviderChannelId { get; set; } = string.Empty;
    public string? OutputGroupName { get; set; }
    public int? ChannelNumber { get; set; }
}

public sealed class UpdateChannelSelectionsRequest
{
    public string ChannelMode { get; set; } = "all";
    public List<ChannelSelectionItem> Channels { get; set; } = [];
}
