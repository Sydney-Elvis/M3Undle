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
            .Include(x => x.ProfileProviders)
                .ThenInclude(pp => pp.Provider)
            .FirstOrDefaultAsync(x => x.IsActive, ct);

        if (activeProfile is null)
            return new ChannelMappingStatsDto();

        var profileId = activeProfile.ProfileId;
        var provider = activeProfile.ProfileProviders
            .Where(pp => pp.Enabled)
            .OrderBy(pp => pp.Priority)
            .Select(pp => pp.Provider)
            .FirstOrDefault(p => p.Enabled);

        var groupsIncluded = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .CountAsync(x => x.ProfileId == profileId
                             && x.ProviderGroup.ContentType == "live"
                             && (x.Decision == LineupReviewSemantics.GroupDecisionInclude
                                 || (x.Decision == LineupReviewSemantics.GroupDecisionLegacyHold && !x.IsNew)), ct);

        var groupsHold = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .CountAsync(x => x.ProfileId == profileId
                             && x.ProviderGroup.ContentType == "live"
                             && (x.Decision == LineupReviewSemantics.GroupDecisionPending
                                 || (x.Decision == LineupReviewSemantics.GroupDecisionLegacyHold && x.IsNew)), ct);

        var groupsNew = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .CountAsync(x => x.ProfileId == profileId
                             && x.ProviderGroup.ContentType == "live"
                             && (x.Decision == LineupReviewSemantics.GroupDecisionPending
                                 || (x.Decision == LineupReviewSemantics.GroupDecisionLegacyHold && x.IsNew))
                             && x.TrackNewChannels, ct);

        var pendingChannelsTotal = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Include(x => x.ProfileGroupFilter).ThenInclude(f => f.ProviderGroup)
            .Include(x => x.ProviderChannel)
            .CountAsync(x => x.ProfileGroupFilter.ProfileId == profileId
                             && x.ProfileGroupFilter.TrackingPolicy == LineupReviewSemantics.TrackingPolicyReview
                             && x.State == LineupReviewSemantics.ChannelStatePending
                             && x.ProviderChannel.Active
                             && x.ProviderChannel.ContentType == "live"
                             && !x.ProviderChannel.IsPlaceholder
                             && x.ProfileGroupFilter.ProviderGroup.ContentType == "live", ct);

        var pendingChannelsNotified = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Include(x => x.ProfileGroupFilter).ThenInclude(f => f.ProviderGroup)
            .Include(x => x.ProviderChannel)
            .CountAsync(x => x.ProfileGroupFilter.ProfileId == profileId
                             && x.ProfileGroupFilter.TrackingPolicy == LineupReviewSemantics.TrackingPolicyReview
                             && x.State == LineupReviewSemantics.ChannelStatePending
                             && x.ProviderChannel.Active
                             && x.ProviderChannel.ContentType == "live"
                             && !x.ProviderChannel.IsPlaceholder
                             && x.ProfileGroupFilter.ProviderGroup.ContentType == "live"
                             && x.ProfileGroupFilter.TrackNewChannels, ct);

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

            vodGroups = await db.ProviderGroups
                .AsNoTracking()
                .CountAsync(x => x.ProviderId == provider.ProviderId && x.Active && x.ContentType == "vod", ct);

            seriesGroups = await db.ProviderGroups
                .AsNoTracking()
                .CountAsync(x => x.ProviderId == provider.ProviderId && x.Active && x.ContentType == "series", ct);
        }

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
