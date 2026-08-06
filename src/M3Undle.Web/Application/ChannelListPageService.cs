using M3Undle.Core.M3u;
using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public sealed class ChannelListPageService(
    IServiceScopeFactory scopeFactory,
    AppEventBus eventBus)
{
    public async Task<string?> GetDefaultProfileIdAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await GetActiveProfileIdAsync(db, cancellationToken);
    }

    public async Task<List<string>> GetMappedGroupsAsync(string profileId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .Where(x => x.ProfileId == profileId
                        && x.Decision != LineupReviewSemantics.GroupDecisionExclude
                        && x.ChannelFilters.Any())
            .Select(x => x.OutputName ?? x.ProviderGroup.RawName)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
    }

    public async Task<ChannelListResponse?> GetChannelsAsync(
        string profileId,
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

        var snapshot = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId && x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot is null
            || string.IsNullOrEmpty(snapshot.ChannelIndexPath)
            || !File.Exists(snapshot.ChannelIndexPath))
            return null;

        var term = search?.Trim();
        var groupFilter = group?.Trim();
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

        // The index is stored in build order (grouped, then pinned/auto-numbered within
        // each group) — sort by the actual channel number so the list matches lineup order.
        var ordered = all
            .OrderBy(x => x.ChannelNumber.HasValue ? 0 : 1)
            .ThenBy(x => x.ChannelNumber)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new ChannelListResponse
        {
            Total = ordered.Count,
            Page = page,
            PageSize = pageSize,
            Items = items,
        };
    }

    public async Task<bool?> UpdateOutputChannelAsync(
        string profileId,
        string providerChannelId,
        UpdateOutputChannelRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

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

        if (request.ClearTvgIdOverride)
            channelFilter.TvgIdOverride = null;
        else if (request.TvgIdOverride is not null)
            channelFilter.TvgIdOverride = string.IsNullOrWhiteSpace(request.TvgIdOverride)
                ? null
                : request.TvgIdOverride.Trim();

        channelFilter.State = LineupReviewSemantics.ChannelStateIncluded;
        channelFilter.UpdatedUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        eventBus.Publish(AppEventKind.GroupFiltersChanged);
        return true;
    }

    public async Task<List<NumberManagerChannelDto>> GetNumberManagerChannelsAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var rows = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Include(x => x.ProfileGroupFilter)
            .ThenInclude(f => f.ProviderGroup)
            .Include(x => x.ProviderChannel)
            .Where(x => x.ProfileGroupFilter.ProfileId == profileId
                        && x.ProfileGroupFilter.Decision != LineupReviewSemantics.GroupDecisionExclude
                        && x.State == LineupReviewSemantics.ChannelStateIncluded
                        && x.ProviderChannel.Active
                        && x.ProviderChannel.ContentType == "live")
            .Select(x => new NumberManagerChannelDto
            {
                ProviderChannelId = x.ProviderChannelId,
                DisplayName = x.ProviderChannel.DisplayName,
                GroupTitle = x.ProfileGroupFilter.OutputName ?? x.ProfileGroupFilter.ProviderGroup.RawName,
                ChannelNumber = x.ChannelNumber,
            })
            .ToListAsync(cancellationToken);

        rows.Sort((a, b) =>
        {
            if (a.ChannelNumber is null && b.ChannelNumber is null)
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal);
            if (a.ChannelNumber is null) return 1;
            if (b.ChannelNumber is null) return -1;
            var cmp = a.ChannelNumber.Value.CompareTo(b.ChannelNumber.Value);
            return cmp != 0 ? cmp : string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal);
        });

        return rows;
    }

    public async Task<bool> BulkUpdateChannelNumbersAsync(
        string profileId,
        BulkChannelNumbersRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (request.Channels is not { Count: > 0 })
            return true;

        var ids = request.Channels.Select(c => c.ProviderChannelId).ToList();

        var filters = await db.ProfileGroupChannelFilters
            .Include(x => x.ProfileGroupFilter)
            .Where(x => ids.Contains(x.ProviderChannelId)
                        && x.ProfileGroupFilter.ProfileId == profileId)
            .ToListAsync(cancellationToken);

        var lookup = filters.ToDictionary(f => f.ProviderChannelId);

        foreach (var item in request.Channels)
        {
            if (!lookup.TryGetValue(item.ProviderChannelId, out var f))
                continue;
            f.ChannelNumber = item.ChannelNumber;
            f.State = LineupReviewSemantics.ChannelStateIncluded;
            f.UpdatedUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        eventBus.Publish(AppEventKind.GroupFiltersChanged);
        return true;
    }

    public async Task<bool?> RemoveOutputChannelAsync(
        string profileId,
        string providerChannelId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

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

    public async Task<string?> GetTvgIdOverrideAsync(
        string profileId,
        string providerChannelId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Include(x => x.ProfileGroupFilter)
            .Where(x => x.ProviderChannelId == providerChannelId
                        && x.ProfileGroupFilter.ProfileId == profileId)
            .Select(x => x.TvgIdOverride)
            .FirstOrDefaultAsync(cancellationToken);
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

    private static Task<string?> GetActiveProfileIdAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        return db.Profiles
            .AsNoTracking()
            .Where(x => x.IsActive)
            .Select(x => (string?)x.ProfileId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
