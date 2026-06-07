using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

internal sealed class ChannelStatsService(IServiceScopeFactory scopeFactory)
{
    public async Task<ChannelMappingStatsDto> GetStatsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var activeProfile = await db.Profiles
            .AsNoTracking()
            .Where(x => x.IsActive)
            .Select(x => new { x.ProfileId })
            .FirstOrDefaultAsync(ct);

        if (activeProfile is null)
            return new ChannelMappingStatsDto();

        var profileId = activeProfile.ProfileId;
        var provider = await db.ProfileProviders
            .AsNoTracking()
            .Where(pp => pp.ProfileId == profileId && pp.Enabled && pp.Provider.Enabled)
            .OrderBy(pp => pp.Priority)
            .Select(pp => new
            {
                pp.ProviderId,
                pp.Provider.IncludeVod,
                pp.Provider.IncludeSeries,
            })
            .FirstOrDefaultAsync(ct);

        var groupAggregates = await db.ProfileGroupFilters
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId && x.ProviderGroup.ContentType == "live")
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Included = g.Count(x => x.Decision != LineupReviewSemantics.GroupDecisionExclude),
                Hold = g.Count(x => x.IsNew),
                New = g.Count(x => x.IsNew && x.TrackNewChannels),
            })
            .FirstOrDefaultAsync(ct);

        var pendingAggregates = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Where(x => x.ProfileGroupFilter.ProfileId == profileId
                        && x.ProfileGroupFilter.TrackingPolicy == LineupReviewSemantics.TrackingPolicyReview
                        && x.State == LineupReviewSemantics.ChannelStatePending
                        && x.ProviderChannel.Active
                        && x.ProviderChannel.ContentType == "live"
                        && !x.ProviderChannel.IsPlaceholder
                        && x.ProfileGroupFilter.ProviderGroup.ContentType == "live")
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Notified = g.Count(x => x.ProfileGroupFilter.TrackNewChannels),
            })
            .FirstOrDefaultAsync(ct);

        var activeSnapshot = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId && x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(ct);

        int? channelsInProvider = null;
        int vodGroups = 0;
        int seriesGroups = 0;

        if (provider is not null)
        {
            var lastFetchRun = await db.FetchRuns
                .AsNoTracking()
                .Where(x => x.ProviderId == provider.ProviderId && x.Status == "ok")
                .OrderByDescending(x => x.StartedUtc)
                .FirstOrDefaultAsync(ct);

            channelsInProvider = lastFetchRun?.ChannelCountSeen;

            var groupTypeCounts = await db.ProviderGroups
                .AsNoTracking()
                .Where(x => x.ProviderId == provider.ProviderId && x.Active
                            && (x.ContentType == "vod" || x.ContentType == "series"))
                .GroupBy(x => x.ContentType)
                .Select(g => new { ContentType = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            vodGroups = groupTypeCounts.FirstOrDefault(x => x.ContentType == "vod")?.Count ?? 0;
            seriesGroups = groupTypeCounts.FirstOrDefault(x => x.ContentType == "series")?.Count ?? 0;
        }

        var groupsIncluded = groupAggregates?.Included ?? 0;
        var groupsHold = groupAggregates?.Hold ?? 0;
        var groupsNew = groupAggregates?.New ?? 0;
        var pendingChannelsTotal = pendingAggregates?.Total ?? 0;
        var pendingChannelsNotified = pendingAggregates?.Notified ?? 0;

        return new ChannelMappingStatsDto
        {
            ProfileId = profileId,
            GroupsIncluded = groupsIncluded,
            GroupsHold = groupsHold,
            GroupsPending = groupsHold,
            GroupsNew = groupsNew,
            PendingChannelsTotal = pendingChannelsTotal,
            PendingChannelsNotified = pendingChannelsNotified,
            ChannelsInOutput = activeSnapshot?.LiveChannelCount ?? 0,
            VodItemsInOutput = activeSnapshot?.VodChannelCount ?? 0,
            SeriesItemsInOutput = activeSnapshot?.SeriesChannelCount ?? 0,
            VodEnabled = provider?.IncludeVod ?? false,
            SeriesEnabled = provider?.IncludeSeries ?? false,
            ChannelsInProvider = channelsInProvider,
            VodGroupsInProvider = vodGroups,
            SeriesGroupsInProvider = seriesGroups,
        };
    }
}
