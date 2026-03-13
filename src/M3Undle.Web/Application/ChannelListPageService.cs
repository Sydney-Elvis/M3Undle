using M3Undle.Core.M3u;
using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public sealed class ChannelListPageService(
    IServiceScopeFactory scopeFactory,
    AppEventBus eventBus)
{
    public async Task<List<string>> GetMappedGroupsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profileId = await GetActiveProfileIdAsync(db, cancellationToken);
        if (profileId is null)
            return [];

        return await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .Where(x => x.ProfileId == profileId && x.Decision != "exclude" && x.ChannelFilters.Any())
            .Select(x => x.OutputName ?? x.ProviderGroup.RawName)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
    }

    public async Task<ChannelListResponse?> GetChannelsAsync(
        int page,
        int pageSize,
        string? search,
        string? group,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        pageSize = Math.Clamp(pageSize, 10, 200);
        page = Math.Max(1, page);

        var provider = await db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, cancellationToken);

        if (provider is null)
            return null;

        var profileLink = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Enabled)
            .OrderBy(x => x.Priority)
            .FirstOrDefaultAsync(cancellationToken);

        if (profileLink is null)
            return null;

        var snapshot = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.ProfileId == profileLink.ProfileId && x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot is null
            || string.IsNullOrEmpty(snapshot.ChannelIndexPath)
            || !File.Exists(snapshot.ChannelIndexPath))
            return null;

        var term = search?.Trim();
        var groupFilter = group?.Trim();
        var hasFilters = !string.IsNullOrEmpty(term) || !string.IsNullOrEmpty(groupFilter);

        if (!hasFilters)
        {
            var total = snapshot.LiveChannelCount;
            var skip = (page - 1) * pageSize;
            var items = new List<ChannelListItemDto>(pageSize);
            var liveCount = 0;

            await foreach (var e in ChannelIndexStore.StreamAllAsync(snapshot.ChannelIndexPath, cancellationToken))
            {
                if (LiveClassifier.ClassifyContent(e.StreamUrl) != "live")
                    continue;

                liveCount++;
                if (liveCount <= skip)
                    continue;

                items.Add(MapEntry(e));
                if (items.Count >= pageSize)
                    break;
            }

            return new ChannelListResponse
            {
                Total = total,
                Page = page,
                PageSize = pageSize,
                Items = items,
            };
        }

        var termUpper = term?.ToUpperInvariant();
        var all = new List<ChannelListItemDto>();

        await foreach (var e in ChannelIndexStore.StreamAllAsync(snapshot.ChannelIndexPath, cancellationToken))
        {
            if (LiveClassifier.ClassifyContent(e.StreamUrl) != "live")
                continue;

            if (!string.IsNullOrEmpty(groupFilter)
                && !string.Equals(e.GroupTitle, groupFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(termUpper)
                && !e.DisplayName.Contains(termUpper, StringComparison.OrdinalIgnoreCase)
                && !(e.TvgId?.Contains(termUpper, StringComparison.OrdinalIgnoreCase) == true)
                && !(e.GroupTitle?.Contains(termUpper, StringComparison.OrdinalIgnoreCase) == true))
                continue;

            all.Add(MapEntry(e));
        }

        var filteredItems = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new ChannelListResponse
        {
            Total = all.Count,
            Page = page,
            PageSize = pageSize,
            Items = filteredItems,
        };
    }

    public async Task<bool?> UpdateOutputChannelAsync(
        string providerChannelId,
        UpdateOutputChannelRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profileId = await GetActiveProfileIdAsync(db, cancellationToken);
        if (profileId is null)
            return null;

        var channelFilter = await db.ProfileGroupChannelFilters
            .Include(x => x.ProfileGroupFilter)
            .FirstOrDefaultAsync(
                x => x.ProviderChannelId == providerChannelId
                     && x.ProfileGroupFilter.ProfileId == profileId,
                cancellationToken);

        if (channelFilter is null)
            return false;

        if (request.ClearChannelNumber)
            channelFilter.ChannelNumber = null;
        else if (request.ChannelNumber is not null)
            channelFilter.ChannelNumber = request.ChannelNumber;

        if (request.ClearOutputGroupName)
            channelFilter.OutputGroupName = null;
        else if (request.OutputGroupName is not null)
            channelFilter.OutputGroupName = string.IsNullOrWhiteSpace(request.OutputGroupName)
                ? null
                : request.OutputGroupName.Trim();

        await db.SaveChangesAsync(cancellationToken);
        eventBus.Publish(AppEventKind.GroupFiltersChanged);
        return true;
    }

    public async Task<bool?> RemoveOutputChannelAsync(string providerChannelId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profileId = await GetActiveProfileIdAsync(db, cancellationToken);
        if (profileId is null)
            return null;

        var channelFilter = await db.ProfileGroupChannelFilters
            .Include(x => x.ProfileGroupFilter)
            .FirstOrDefaultAsync(
                x => x.ProviderChannelId == providerChannelId
                     && x.ProfileGroupFilter.ProfileId == profileId,
                cancellationToken);

        if (channelFilter is null)
            return false;

        db.ProfileGroupChannelFilters.Remove(channelFilter);
        await db.SaveChangesAsync(cancellationToken);
        eventBus.Publish(AppEventKind.GroupFiltersChanged);
        return true;
    }

    private static ChannelListItemDto MapEntry(ChannelIndexEntry e) => new()
    {
        ChannelNumber = e.TvgChno,
        DisplayName = e.DisplayName,
        LogoUrl = e.LogoUrl,
        GroupTitle = e.GroupTitle,
        TvgId = e.TvgId,
        StreamKey = e.StreamKey,
        ProviderChannelId = e.ProviderChannelId,
    };

    private static async Task<string?> GetActiveProfileIdAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var provider = await db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, cancellationToken);

        if (provider is null)
            return null;

        var profileLink = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Enabled)
            .OrderBy(x => x.Priority)
            .FirstOrDefaultAsync(cancellationToken);

        return profileLink?.ProfileId;
    }
}
