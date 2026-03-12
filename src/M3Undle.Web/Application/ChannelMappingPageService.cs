using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public sealed class ChannelMappingPageService(
    IServiceScopeFactory scopeFactory,
    IRefreshTrigger refreshTrigger,
    AppEventBus eventBus)
{
    public async Task<ActiveProfileDto?> GetActiveProfileAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

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

        var profile = await db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProfileId == profileLink.ProfileId && x.Enabled, cancellationToken);

        if (profile is null)
            return null;

        return new ActiveProfileDto
        {
            ProfileId = profile.ProfileId,
            Name = profile.Name,
        };
    }

    public async Task<List<GroupFilterDto>> ListGroupFiltersAsync(string profileId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var filters = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup).ThenInclude(g => g.Provider)
            .Include(x => x.ChannelFilters)
            .Where(x => x.ProfileId == profileId)
            .ToListAsync(cancellationToken);

        return filters
            .OrderBy(f => f.Decision == "hold" ? 0 : 1)
            .ThenByDescending(f => f.IsNew)
            .ThenBy(f => f.ProviderGroup.RawName, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToList();
    }

    public bool TriggerBuildOnly()
    {
        return refreshTrigger.TriggerBuildOnly();
    }

    public async Task<bool> UpdateGroupFilterAsync(
        string profileId,
        string filterId,
        UpdateGroupFilterRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var filter = await db.ProfileGroupFilters
            .Include(x => x.ProviderGroup).ThenInclude(g => g.Provider)
            .FirstOrDefaultAsync(x => x.ProfileGroupFilterId == filterId && x.ProfileId == profileId, cancellationToken);

        if (filter is null)
            return false;

        if (request.Decision is not null)
        {
            filter.Decision = request.Decision;
            filter.IsNew = false;
        }

        if (request.ClearIsNew)
            filter.IsNew = false;

        if (request.ClearOutputName)
            filter.OutputName = null;
        else if (request.OutputName is not null)
            filter.OutputName = string.IsNullOrWhiteSpace(request.OutputName) ? null : request.OutputName.Trim();

        if (request.ClearAutoNum)
        {
            filter.AutoNumStart = null;
            filter.AutoNumEnd = null;
        }
        else
        {
            if (request.AutoNumStart is not null)
                filter.AutoNumStart = request.AutoNumStart;
            if (request.ClearAutoNumEnd)
                filter.AutoNumEnd = null;
            else if (request.AutoNumEnd is not null)
                filter.AutoNumEnd = request.AutoNumEnd;
        }

        if (request.TrackNewChannels is not null)
            filter.TrackNewChannels = request.TrackNewChannels.Value;

        if (request.SortOverride is not null)
            filter.SortOverride = request.SortOverride;

        filter.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (request.Decision == "exclude")
        {
            await db.ProviderChannels
                .Where(x => x.ProviderGroupId == filter.ProviderGroupId && x.ContentType == "live")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false), cancellationToken);
        }

        if (request.Decision is not null)
            eventBus.Publish(AppEventKind.GroupFiltersChanged);

        return true;
    }

    public async Task<int> DismissNewGroupsAsync(string profileId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ProfileGroupFilters
            .Where(x => x.ProfileId == profileId && x.IsNew)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsNew, false), cancellationToken);
    }

    public async Task<int> BulkDecideAsync(
        string profileId,
        string decision,
        List<string> providerGroupIds,
        CancellationToken cancellationToken)
    {
        if (decision is not ("exclude" or "hold"))
            return 0;

        if (providerGroupIds.Count == 0)
            return 0;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var existingFilters = await db.ProfileGroupFilters
            .Where(x => x.ProfileId == profileId && providerGroupIds.Contains(x.ProviderGroupId))
            .ToListAsync(cancellationToken);

        var existingByGroupId = existingFilters.ToDictionary(x => x.ProviderGroupId, StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        var updated = 0;

        foreach (var groupId in providerGroupIds)
        {
            if (existingByGroupId.TryGetValue(groupId, out var filter))
            {
                filter.Decision = decision;
                filter.IsNew = false;
                filter.UpdatedUtc = now;
            }
            else
            {
                db.ProfileGroupFilters.Add(new ProfileGroupFilter
                {
                    ProfileGroupFilterId = Guid.NewGuid().ToString(),
                    ProfileId = profileId,
                    ProviderGroupId = groupId,
                    Decision = decision,
                    IsNew = false,
                    ChannelMode = "select",
                    TrackNewChannels = false,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                });
            }
            updated++;
        }

        await db.SaveChangesAsync(cancellationToken);

        if (decision == "exclude")
        {
            var groupIds = providerGroupIds;
            await db.ProviderChannels
                .Where(x => x.ProviderGroupId != null && groupIds.Contains(x.ProviderGroupId!) && x.ContentType == "live")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false), cancellationToken);
        }

        eventBus.Publish(AppEventKind.GroupFiltersChanged);
        return updated;
    }

    public async Task<List<ChannelSearchGroupResult>> SearchChannelsAsync(
        string profileId,
        string? q,
        CancellationToken cancellationToken)
    {
        var term = q?.Trim().Replace("*", string.Empty).Replace("!", string.Empty).ToUpperInvariant() ?? string.Empty;
        if (term.Length < 2)
            return [];

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var matches = await db.ProviderChannels
            .AsNoTracking()
            .Where(c => c.Active && EF.Functions.Like(c.DisplayName.ToUpper(), $"%{term}%"))
            .Join(
                db.ProfileGroupFilters.Where(f => f.ProfileId == profileId),
                c => c.ProviderGroupId,
                f => f.ProviderGroupId,
                (c, f) => new { FilterId = f.ProfileGroupFilterId, c.ProviderChannelId, c.DisplayName })
            .OrderBy(x => x.DisplayName)
            .Take(500)
            .ToListAsync(cancellationToken);

        return matches
            .GroupBy(x => x.FilterId)
            .Select(g => new ChannelSearchGroupResult
            {
                FilterId = g.Key,
                Channels = g.Select(x => new ChannelSearchItemDto
                {
                    ProviderChannelId = x.ProviderChannelId,
                    DisplayName = x.DisplayName,
                }).ToList(),
            })
            .ToList();
    }

    public async Task<ChannelSelectionsDto?> GetChannelSelectionsAsync(
        string profileId,
        string filterId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var filter = await db.ProfileGroupFilters
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProfileGroupFilterId == filterId && x.ProfileId == profileId, cancellationToken);

        if (filter is null)
            return null;

        var allChannels = await db.ProviderChannels
            .AsNoTracking()
            .Where(x => x.ProviderGroupId == filter.ProviderGroupId && x.Active)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        var existingSelections = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Where(x => x.ProfileGroupFilterId == filterId)
            .ToListAsync(cancellationToken);

        var selectionByChannelId = existingSelections.ToDictionary(x => x.ProviderChannelId, StringComparer.Ordinal);

        var channels = allChannels.Select(ch =>
        {
            selectionByChannelId.TryGetValue(ch.ProviderChannelId, out var sel);
            return new ProviderChannelSelectDto
            {
                ProviderChannelId = ch.ProviderChannelId,
                DisplayName = ch.DisplayName,
                TvgId = ch.TvgId,
                Active = ch.Active,
                IsSelected = sel is not null,
                OutputGroupName = sel?.OutputGroupName,
                ChannelNumber = sel?.ChannelNumber,
            };
        }).ToList();

        return new ChannelSelectionsDto
        {
            ChannelMode = filter.ChannelMode,
            Channels = channels,
        };
    }

    public async Task<bool> UpdateChannelSelectionsAsync(
        string profileId,
        string filterId,
        UpdateChannelSelectionsRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var filter = await db.ProfileGroupFilters
            .FirstOrDefaultAsync(x => x.ProfileGroupFilterId == filterId && x.ProfileId == profileId, cancellationToken);

        if (filter is null)
            return false;

        filter.ChannelMode = request.ChannelMode is "all" or "select" ? request.ChannelMode : "all";
        filter.UpdatedUtc = DateTime.UtcNow;

        await db.ProfileGroupChannelFilters
            .Where(x => x.ProfileGroupFilterId == filterId)
            .ExecuteDeleteAsync(cancellationToken);

        if (filter.ChannelMode == "select" && request.Channels.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var item in request.Channels)
            {
                db.ProfileGroupChannelFilters.Add(new ProfileGroupChannelFilter
                {
                    ProfileGroupChannelFilterId = Guid.NewGuid().ToString(),
                    ProfileGroupFilterId = filterId,
                    ProviderChannelId = item.ProviderChannelId,
                    OutputGroupName = string.IsNullOrWhiteSpace(item.OutputGroupName) ? null : item.OutputGroupName.Trim(),
                    ChannelNumber = item.ChannelNumber,
                    CreatedUtc = now,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        eventBus.Publish(AppEventKind.GroupFiltersChanged);
        return true;
    }

    private static GroupFilterDto ToDto(ProfileGroupFilter f)
    {
        var selectedCount = f.ChannelFilters?.Count ?? 0;

        return new GroupFilterDto
        {
            ProfileGroupFilterId = f.ProfileGroupFilterId,
            ProviderGroupId = f.ProviderGroupId,
            ProviderGroupRawName = f.ProviderGroup.RawName,
            ProviderGroupContentType = f.ProviderGroup.ContentType,
            ProviderGroupFirstSeen = f.ProviderGroup.FirstSeenUtc,
            ProviderGroupLastSeen = f.ProviderGroup.LastSeenUtc,
            ProviderGroupActive = f.ProviderGroup.Active,
            ProviderName = f.ProviderGroup.Provider?.Name ?? string.Empty,
            Decision = f.Decision,
            IsNew = f.IsNew,
            OutputName = f.OutputName,
            AutoNumStart = f.AutoNumStart,
            AutoNumEnd = f.AutoNumEnd,
            ChannelMode = f.ChannelMode,
            TrackNewChannels = f.TrackNewChannels,
            SortOverride = f.SortOverride,
            SelectedChannelCount = selectedCount,
            ChannelCount = f.ProviderGroup.ChannelCount,
        };
    }
}
