namespace M3Undle.Web.Contracts;

public sealed class GroupFilterDto
{
    public string ProfileGroupFilterId { get; set; } = string.Empty;
    public string ProviderGroupId { get; set; } = string.Empty;
    public string ProviderGroupRawName { get; set; } = string.Empty;
    public bool ProviderGroupActive { get; set; }
    public string ProviderGroupContentType { get; set; } = "live";
    public DateTime ProviderGroupFirstSeen { get; set; }
    public DateTime ProviderGroupLastSeen { get; set; }
    public int? ChannelCount { get; set; }
    public int SelectedChannelCount { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string Decision { get; set; } = "pending";
    public bool IsNew { get; set; }
    public string ChannelMode { get; set; } = "select";
    public string TrackingPolicy { get; set; } = "review"; // review | notify | auto_add_all | auto_add_matching
    public string? TrackingKeywords { get; set; }
    public string? OutputName { get; set; }
    public int? AutoNumStart { get; set; }
    public int? AutoNumEnd { get; set; }
    public bool TrackNewChannels { get; set; } // notify (true) | mute (false)
    public int? SortOverride { get; set; }
}

public sealed class UpdateGroupFilterRequest
{
    public string? Decision { get; set; }
    public string? ChannelMode { get; set; } // select (manual review) | all (auto-update)
    public string? TrackingPolicy { get; set; }
    public string? TrackingKeywords { get; set; }
    public bool ClearTrackingKeywords { get; set; }
    public bool ClearIsNew { get; set; }
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
    public int GroupsHold { get; set; } // legacy field kept for existing UI consumers
    public int GroupsPending { get; set; }
    public int GroupsNew { get; set; }
    public int PendingChannelsTotal { get; set; }
    public int PendingChannelsNotified { get; set; }
    public int ChannelsInOutput { get; set; } // Live channels in output
    public int VodItemsInOutput { get; set; }
    public int SeriesItemsInOutput { get; set; }
    public bool VodEnabled { get; set; }
    public bool SeriesEnabled { get; set; }
    public int? ChannelsInProvider { get; set; }
    public int VodGroupsInProvider { get; set; }
    public int SeriesGroupsInProvider { get; set; }
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
    public string? GroupTitle { get; set; }
    public string? EventContentKey { get; set; }
    public bool Active { get; set; }
    public bool IsSelected { get; set; }
    public string State { get; set; } = "included";
    public string? DisplayNameOverride { get; set; }
    public string? OutputGroupName { get; set; }
    public int? ChannelNumber { get; set; }
}

public sealed class ChannelSelectionsDto
{
    public string ChannelMode { get; set; } = "select";
    public List<ProviderChannelSelectDto> Channels { get; set; } = [];
}

public sealed class ChannelSelectionItem
{
    public string ProviderChannelId { get; set; } = string.Empty;
    public string? State { get; set; }
    public string? DisplayNameOverride { get; set; }
    public string? OutputGroupName { get; set; }
    public int? ChannelNumber { get; set; }
}

public sealed class UpdateChannelSelectionsRequest
{
    public string ChannelMode { get; set; } = "select";
    public List<ChannelSelectionItem> Channels { get; set; } = [];
}

public sealed class ChannelSearchItemDto
{
    public string ProviderChannelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class ChannelSearchGroupResult
{
    public string FilterId { get; set; } = string.Empty;
    public List<ChannelSearchItemDto> Channels { get; set; } = [];
}

public sealed class ChannelListItemDto
{
    public int? ChannelNumber { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string? GroupTitle { get; set; }
    public string? TvgId { get; set; }
    public string? TvgIdOverride { get; set; }
    public string StreamKey { get; set; } = string.Empty;
    public string ProviderChannelId { get; set; } = string.Empty;
}

public sealed class UpdateOutputChannelRequest
{
    public int? ChannelNumber { get; set; }
    public bool ClearChannelNumber { get; set; }
    public string? OutputGroupName { get; set; }
    public bool ClearOutputGroupName { get; set; }
    public string? TvgIdOverride { get; set; }
    public bool ClearTvgIdOverride { get; set; }
}

public sealed class NumberManagerChannelDto
{
    public string ProviderChannelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? GroupTitle { get; set; }
    public int? ChannelNumber { get; set; }
}

public sealed class BulkChannelNumberItem
{
    public string ProviderChannelId { get; set; } = string.Empty;
    public int? ChannelNumber { get; set; }
}

public sealed class BulkChannelNumbersRequest
{
    public List<BulkChannelNumberItem> Channels { get; set; } = [];
}

public sealed class ChannelListResponse
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<ChannelListItemDto> Items { get; set; } = [];
}

public sealed class ReviewQueueItemDto
{
    public string ProviderChannelId { get; set; } = string.Empty;
    public string ProfileGroupFilterId { get; set; } = string.Empty;
    public string ProviderGroupId { get; set; } = string.Empty;
    public string ProviderGroupRawName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? TvgId { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool Active { get; set; }
    public bool NotifyOnPending { get; set; }
    public string State { get; set; } = "pending";
}

public sealed class ReviewQueueListResponse
{
    public int Total { get; set; }
    public int PendingNotified { get; set; }
    public int PendingTotal { get; set; }
    public List<ReviewQueueItemDto> Items { get; set; } = [];
}

public sealed class BulkReviewQueueStateRequest
{
    public List<string> ProviderChannelIds { get; set; } = [];
    public List<string> ProviderGroupIds { get; set; } = [];
    public string State { get; set; } = string.Empty;
}

public sealed class WhatsOnEventDto
{
    public string DisplayName { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public string? EventContentKey { get; set; }
    public DateTime? EventStartUtc { get; set; }
    public DateTime? EventEndUtc { get; set; }
    public int? ChannelNumber { get; set; }
    public string MatchedKeyword { get; set; } = string.Empty;
}

public sealed class WhatsOnGroupDto
{
    public string GroupName { get; set; } = string.Empty;
    public string TrackingPolicy { get; set; } = string.Empty;
    public List<WhatsOnEventDto> Events { get; set; } = [];
}

public sealed class WhatsOnThisWeekResponse
{
    public string ProfileId { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public DateTime GeneratedUtc { get; set; }
    public int TotalEvents { get; set; }
    public List<WhatsOnGroupDto> Groups { get; set; } = [];
}
