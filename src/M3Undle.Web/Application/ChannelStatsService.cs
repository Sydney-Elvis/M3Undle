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

        var provider = await db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, ct);

        if (provider is null)
            return new ChannelMappingStatsDto();

        var profileLink = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Enabled)
            .OrderBy(x => x.Priority)
            .FirstOrDefaultAsync(ct);

        if (profileLink is null)
            return new ChannelMappingStatsDto();

        var profileId = profileLink.ProfileId;

        var groupsIncluded = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .CountAsync(x => x.ProfileId == profileId && x.ProviderGroup.ContentType == "live" && x.Decision != "exclude" && x.ChannelFilters.Any(), ct);

        var groupsHold = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .CountAsync(x => x.ProfileId == profileId && x.ProviderGroup.ContentType == "live" && x.Decision == "hold" && !x.ChannelFilters.Any(), ct);

        var groupsNew = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .CountAsync(x => x.ProfileId == profileId && x.ProviderGroup.ContentType == "live" && x.IsNew, ct);

        var activeSnapshot = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId && x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(ct);

        var lastFetchRun = await db.FetchRuns
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Status == "ok")
            .OrderByDescending(x => x.StartedUtc)
            .FirstOrDefaultAsync(ct);

        var vodGroups = await db.ProviderGroups
            .AsNoTracking()
            .CountAsync(x => x.ProviderId == provider.ProviderId && x.Active && x.ContentType == "vod", ct);

        var seriesGroups = await db.ProviderGroups
            .AsNoTracking()
            .CountAsync(x => x.ProviderId == provider.ProviderId && x.Active && x.ContentType == "series", ct);

        return new ChannelMappingStatsDto
        {
            ProfileId = profileId,
            GroupsIncluded = groupsIncluded,
            GroupsHold = groupsHold,
            GroupsNew = groupsNew,
            ChannelsInOutput = activeSnapshot?.LiveChannelCount ?? 0,
            VodItemsInOutput = activeSnapshot?.VodChannelCount ?? 0,
            SeriesItemsInOutput = activeSnapshot?.SeriesChannelCount ?? 0,
            VodEnabled = provider.IncludeVod,
            SeriesEnabled = provider.IncludeSeries,
            ChannelsInProvider = lastFetchRun?.ChannelCountSeen,
            VodGroupsInProvider = vodGroups,
            SeriesGroupsInProvider = seriesGroups,
        };
    }
}
