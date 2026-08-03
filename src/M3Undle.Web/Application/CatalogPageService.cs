using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public sealed class CatalogPageService(
    IServiceScopeFactory scopeFactory,
    AppEventBus eventBus)
{
    public async Task<string?> GetDefaultProfileIdAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Profiles
            .AsNoTracking()
            .Where(x => x.Enabled && x.IsActive)
            .Select(x => x.ProfileId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<CatalogGroupDto>> ListGroupsAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ProfileProviders
            .AsNoTracking()
            .Where(link => link.ProfileId == profileId)
            .SelectMany(
                link => link.Provider.ProviderGroups
                    .Where(group => group.Active
                                    && (group.ContentType == "vod" || group.ContentType == "series")),
                (link, group) => new CatalogGroupDto
                {
                    ProviderGroupId = group.ProviderGroupId,
                    ProviderId = link.ProviderId,
                    ProviderName = link.Provider.Name,
                    Name = group.RawName,
                    ContentType = group.ContentType,
                    ItemCount = group.ChannelCount ?? 0,
                    TitleCount = group.CatalogItems.Count(item => item.Active),
                    FirstSeenUtc = group.FirstSeenUtc,
                    LastSeenUtc = group.LastSeenUtc,
                    ProviderEnabled = link.Provider.Enabled,
                    ProfileProviderEnabled = link.Enabled,
                    ContentTypeEnabled = group.ContentType == "vod"
                        ? link.Provider.IncludeVod
                        : link.Provider.IncludeSeries,
                    Decision = group.ProfileCatalogGroupFilters
                        .Where(filter => filter.ProfileId == profileId)
                        .Select(filter => filter.Decision)
                        .FirstOrDefault() ?? LineupReviewSemantics.GroupDecisionInclude,
                    IsNew = group.ProfileCatalogGroupFilters
                        .Any(filter => filter.ProfileId == profileId && filter.IsNew),
                })
            .OrderBy(x => x.ProviderName)
            .ThenBy(x => x.ContentType)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<CatalogItemsResponse?> GetItemsAsync(
        string profileId,
        string providerGroupId,
        int page,
        int pageSize,
        string? search,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var group = await db.ProviderGroups
            .AsNoTracking()
            .Where(x => x.ProviderGroupId == providerGroupId
                        && x.Provider.ProfileProviders.Any(link => link.ProfileId == profileId)
                        && (x.ContentType == "vod" || x.ContentType == "series"))
            .Select(x => new
            {
                x.ProviderGroupId,
                GroupName = x.RawName,
                ProviderName = x.Provider.Name,
                x.ContentType,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (group is null)
            return null;

        var query = db.CatalogItems
            .AsNoTracking()
            .Where(x => x.ProviderGroupId == providerGroupId && x.Active);
        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            var escaped = term
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            query = query.Where(x => EF.Functions.Like(x.Title, $"%{escaped}%", "\\"));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.Title)
            .ThenBy(x => x.CatalogItemId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new CatalogItemDto
            {
                CatalogItemId = x.CatalogItemId,
                Title = x.Title,
                ContentType = x.ContentType,
                EpisodeCount = x.EpisodeCount,
                LastSeenUtc = x.LastSeenUtc,
            })
            .ToListAsync(cancellationToken);

        return new CatalogItemsResponse
        {
            ProviderGroupId = group.ProviderGroupId,
            GroupName = group.GroupName,
            ProviderName = group.ProviderName,
            ContentType = group.ContentType,
            Total = total,
            Page = page,
            PageSize = pageSize,
            Items = items,
        };
    }

    public async Task<CatalogTitleSearchResponse> SearchItemsAsync(
        string profileId,
        string? contentType,
        int page,
        int pageSize,
        string? search,
        string? groupSearch,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var query = db.CatalogItems
            .AsNoTracking()
            .Where(item => item.Active
                           && item.ProviderGroup.Active
                           && item.Provider.ProfileProviders.Any(link => link.ProfileId == profileId));
        if (contentType is "vod" or "series")
            query = query.Where(item => item.ContentType == contentType);

        var groupTerm = groupSearch?.Trim();
        if (!string.IsNullOrWhiteSpace(groupTerm))
        {
            var escapedGroup = EscapeLikePattern(groupTerm);
            query = query.Where(item =>
                EF.Functions.Like(item.ProviderGroup.RawName, $"%{escapedGroup}%", "\\")
                || EF.Functions.Like(item.Provider.Name, $"%{escapedGroup}%", "\\"));
        }

        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            var escaped = EscapeLikePattern(term);
            query = query.Where(item => EF.Functions.Like(item.Title, $"%{escaped}%", "\\"));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(item => item.Title)
            .ThenBy(item => item.ContentType)
            .ThenBy(item => item.ProviderGroup.RawName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new CatalogItemDto
            {
                CatalogItemId = item.CatalogItemId,
                Title = item.Title,
                ContentType = item.ContentType,
                EpisodeCount = item.EpisodeCount,
                LastSeenUtc = item.LastSeenUtc,
                ProviderGroupId = item.ProviderGroupId,
                GroupName = item.ProviderGroup.RawName,
                ProviderName = item.Provider.Name,
            })
            .ToListAsync(cancellationToken);

        return new CatalogTitleSearchResponse
        {
            Total = total,
            Page = page,
            PageSize = pageSize,
            Items = items,
        };
    }

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    public async Task<bool> UpdateDecisionAsync(
        string profileId,
        string providerGroupId,
        string decision,
        CancellationToken cancellationToken)
    {
        if (decision is not (LineupReviewSemantics.GroupDecisionInclude or LineupReviewSemantics.GroupDecisionExclude))
            return false;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var validGroup = await db.ProviderGroups
            .AsNoTracking()
            .AnyAsync(group => group.ProviderGroupId == providerGroupId
                               && (group.ContentType == "vod" || group.ContentType == "series")
                               && group.Provider.ProfileProviders.Any(link => link.ProfileId == profileId),
                cancellationToken);
        if (!validGroup)
            return false;

        var filter = await db.ProfileCatalogGroupFilters
            .SingleOrDefaultAsync(x => x.ProfileId == profileId && x.ProviderGroupId == providerGroupId,
                cancellationToken);
        var now = DateTime.UtcNow;
        if (filter is null)
        {
            filter = new ProfileCatalogGroupFilter
            {
                ProfileCatalogGroupFilterId = Guid.NewGuid().ToString(),
                ProfileId = profileId,
                ProviderGroupId = providerGroupId,
                Decision = decision,
                IsNew = false,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            db.ProfileCatalogGroupFilters.Add(filter);
        }
        else
        {
            filter.Decision = decision;
            filter.IsNew = false;
            filter.UpdatedUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        eventBus.Publish(AppEventKind.GroupFiltersChanged);
        return true;
    }
}
