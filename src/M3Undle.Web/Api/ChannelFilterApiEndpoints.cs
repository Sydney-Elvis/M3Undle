using M3Undle.Web.Application;
using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace M3Undle.Web.Api;

public static class ChannelFilterApiEndpoints
{
    private static readonly JsonSerializerOptions ChannelIndexJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static IEndpointRouteBuilder MapChannelFilterApiEndpoints(this IEndpointRouteBuilder app)
    {
        var profiles = app.MapGroup("/api/v1/profiles");
        profiles.MapGet("/active", GetActiveProfileAsync);
        profiles.MapGet("/{profileId}/group-filters", ListGroupFiltersAsync);
        profiles.MapPatch("/{profileId}/group-filters/{filterId}", UpdateGroupFilterAsync);
        profiles.MapPost("/{profileId}/group-filters/bulk-decide", BulkDecideAsync);
        profiles.MapGet("/{profileId}/group-filters/{filterId}/channels", GetGroupChannelsAsync);
        profiles.MapGet("/{profileId}/group-filters/{filterId}/channel-selections", GetChannelSelectionsAsync);
        profiles.MapPut("/{profileId}/group-filters/{filterId}/channel-selections", UpdateChannelSelectionsAsync);
        profiles.MapGet("/{profileId}/group-filters/{filterId}/raw-provider-m3u", GetRawProviderM3uAsync); // DEBUG - REMOVE

        app.MapGet("/api/v1/channel-stats", GetChannelStatsAsync);

        return app;
    }

    // -------------------------------------------------------------------------
    // Active profile
    // -------------------------------------------------------------------------

    private static async Task<Results<Ok<ActiveProfileDto>, NotFound>> GetActiveProfileAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var provider = await db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, cancellationToken);

        if (provider is null)
            return TypedResults.NotFound();

        var profileLink = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Enabled)
            .OrderBy(x => x.Priority)
            .FirstOrDefaultAsync(cancellationToken);

        if (profileLink is null)
            return TypedResults.NotFound();

        var profile = await db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProfileId == profileLink.ProfileId && x.Enabled, cancellationToken);

        if (profile is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(new ActiveProfileDto
        {
            ProfileId = profile.ProfileId,
            Name = profile.Name,
        });
    }

    // -------------------------------------------------------------------------
    // Group filters
    // -------------------------------------------------------------------------

    private static async Task<Ok<List<GroupFilterDto>>> ListGroupFiltersAsync(
        string profileId,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var filters = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .Where(x => x.ProfileId == profileId)
            .ToListAsync(cancellationToken);

        var dtos = filters
            .OrderBy(f => f.Decision == "pending" ? 0 : f.Decision == "include" ? 1 : 2)
            .ThenBy(f => f.ProviderGroup.RawName, StringComparer.OrdinalIgnoreCase)
            .Select(f => ToDto(f))
            .ToList();

        return TypedResults.Ok(dtos);
    }

    private static async Task<Results<Ok<GroupFilterDto>, NotFound>> UpdateGroupFilterAsync(
        string profileId,
        string filterId,
        UpdateGroupFilterRequest request,
        ApplicationDbContext db,
        AppEventBus eventBus,
        CancellationToken cancellationToken)
    {
        var filter = await db.ProfileGroupFilters
            .Include(x => x.ProviderGroup)
            .FirstOrDefaultAsync(x => x.ProfileGroupFilterId == filterId && x.ProfileId == profileId, cancellationToken);

        if (filter is null)
            return TypedResults.NotFound();

        if (request.Decision is not null)
            filter.Decision = request.Decision;

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
            if (request.AutoNumEnd is not null)
                filter.AutoNumEnd = request.AutoNumEnd;
        }

        if (request.TrackNewChannels is not null)
            filter.TrackNewChannels = request.TrackNewChannels.Value;

        if (request.SortOverride is not null)
            filter.SortOverride = request.SortOverride;

        filter.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (request.Decision == "exclude")
            await db.ProviderChannels
                .Where(x => x.ProviderGroupId == filter.ProviderGroupId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false), cancellationToken);

        if (request.Decision is not null)
            eventBus.Publish(AppEventKind.GroupFiltersChanged);

        return TypedResults.Ok(ToDto(filter));
    }

    private static async Task<Results<Ok<object>, BadRequest<string>>> BulkDecideAsync(
        string profileId,
        BulkGroupDecisionRequest request,
        ApplicationDbContext db,
        AppEventBus eventBus,
        CancellationToken cancellationToken)
    {
        if (request.Decision is not ("include" or "exclude" or "pending"))
            return TypedResults.BadRequest("Decision must be 'include', 'exclude', or 'pending'.");

        if (request.ProviderGroupIds.Count == 0)
            return TypedResults.Ok((object)new { updated = 0 });

        var existingFilters = await db.ProfileGroupFilters
            .Where(x => x.ProfileId == profileId && request.ProviderGroupIds.Contains(x.ProviderGroupId))
            .ToListAsync(cancellationToken);

        var existingByGroupId = existingFilters.ToDictionary(x => x.ProviderGroupId, StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        int updated = 0;

        foreach (var groupId in request.ProviderGroupIds)
        {
            if (existingByGroupId.TryGetValue(groupId, out var filter))
            {
                filter.Decision = request.Decision;
                filter.UpdatedUtc = now;
            }
            else
            {
                db.ProfileGroupFilters.Add(new ProfileGroupFilter
                {
                    ProfileGroupFilterId = Guid.NewGuid().ToString(),
                    ProfileId = profileId,
                    ProviderGroupId = groupId,
                    Decision = request.Decision,
                    TrackNewChannels = false,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                });
            }
            updated++;
        }

        await db.SaveChangesAsync(cancellationToken);

        if (request.Decision == "exclude")
        {
            var groupIds = request.ProviderGroupIds;
            await db.ProviderChannels
                .Where(x => x.ProviderGroupId != null && groupIds.Contains(x.ProviderGroupId!))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false), cancellationToken);
        }

        eventBus.Publish(AppEventKind.GroupFiltersChanged);

        return TypedResults.Ok((object)new { updated });
    }

    // -------------------------------------------------------------------------
    // Group channels (from active snapshot channel_index.json)
    // -------------------------------------------------------------------------

    private static async Task<Results<Ok<GroupChannelsResponse>, NotFound>> GetGroupChannelsAsync(
        string profileId,
        string filterId,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var filter = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .FirstOrDefaultAsync(x => x.ProfileGroupFilterId == filterId && x.ProfileId == profileId, cancellationToken);

        if (filter is null)
            return TypedResults.NotFound();

        if (filter.Decision != "include")
        {
            var providerChannels = await db.ProviderChannels
                .AsNoTracking()
                .Where(x => x.ProviderGroupId == filter.ProviderGroupId && x.Active)
                .OrderBy(x => x.DisplayName)
                .Select(x => new GroupChannelDto { DisplayName = x.DisplayName, TvgId = x.TvgId })
                .ToListAsync(cancellationToken);

            return TypedResults.Ok(new GroupChannelsResponse { IsInOutput = false, Channels = providerChannels });
        }

        var snapshot = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId && x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot is null || string.IsNullOrEmpty(snapshot.ChannelIndexPath) || !File.Exists(snapshot.ChannelIndexPath))
            return TypedResults.Ok(new GroupChannelsResponse { IsInOutput = true });

        var outputName = filter.OutputName ?? filter.ProviderGroup.RawName;

        var json = await File.ReadAllTextAsync(snapshot.ChannelIndexPath, cancellationToken);
        var entries = JsonSerializer.Deserialize<List<ChannelIndexEntry>>(json, ChannelIndexJsonOptions) ?? [];

        var channels = entries
            .Where(e => string.Equals(e.GroupTitle, outputName, StringComparison.Ordinal))
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(e => new GroupChannelDto { Number = e.TvgChno, DisplayName = e.DisplayName, TvgId = e.TvgId })
            .ToList();

        return TypedResults.Ok(new GroupChannelsResponse { IsInOutput = true, Channels = channels });
    }

    // -------------------------------------------------------------------------
    // Channel selections (per-channel filter within an included group)
    // -------------------------------------------------------------------------

    private static async Task<Results<Ok<ChannelSelectionsDto>, NotFound>> GetChannelSelectionsAsync(
        string profileId,
        string filterId,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var filter = await db.ProfileGroupFilters
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProfileGroupFilterId == filterId && x.ProfileId == profileId, cancellationToken);

        if (filter is null)
            return TypedResults.NotFound();

        var allChannels = await db.ProviderChannels
            .AsNoTracking()
            .Where(x => x.ProviderGroupId == filter.ProviderGroupId && x.Active)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        var selectedIds = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Where(x => x.ProfileGroupFilterId == filterId)
            .Select(x => x.ProviderChannelId)
            .ToHashSetAsync(cancellationToken);

        var dtos = allChannels.Select(ch => new ProviderChannelSelectDto
        {
            ProviderChannelId = ch.ProviderChannelId,
            DisplayName = ch.DisplayName,
            TvgId = ch.TvgId,
            Active = ch.Active,
            IsSelected = selectedIds.Contains(ch.ProviderChannelId),
        }).ToList();

        return TypedResults.Ok(new ChannelSelectionsDto
        {
            ChannelMode = filter.ChannelMode,
            Channels = dtos,
        });
    }

    private static async Task<Results<Ok<ChannelSelectionsDto>, NotFound>> UpdateChannelSelectionsAsync(
        string profileId,
        string filterId,
        UpdateChannelSelectionsRequest request,
        ApplicationDbContext db,
        AppEventBus eventBus,
        CancellationToken cancellationToken)
    {
        var filter = await db.ProfileGroupFilters
            .FirstOrDefaultAsync(x => x.ProfileGroupFilterId == filterId && x.ProfileId == profileId, cancellationToken);

        if (filter is null)
            return TypedResults.NotFound();

        filter.ChannelMode = request.ChannelMode is "all" or "select" ? request.ChannelMode : "all";
        filter.UpdatedUtc = DateTime.UtcNow;

        await db.ProfileGroupChannelFilters
            .Where(x => x.ProfileGroupFilterId == filterId)
            .ExecuteDeleteAsync(cancellationToken);

        if (filter.ChannelMode == "select" && request.ProviderChannelIds.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var channelId in request.ProviderChannelIds)
            {
                db.ProfileGroupChannelFilters.Add(new Data.Entities.ProfileGroupChannelFilter
                {
                    ProfileGroupChannelFilterId = Guid.NewGuid().ToString(),
                    ProfileGroupFilterId = filterId,
                    ProviderChannelId = channelId,
                    CreatedUtc = now,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        eventBus.Publish(AppEventKind.GroupFiltersChanged);

        return await GetChannelSelectionsAsync(profileId, filterId, db, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // DEBUG - REMOVE: Raw provider M3U (returns actual provider stream URLs)
    // -------------------------------------------------------------------------

    private static async Task<Results<ContentHttpResult, NotFound>> GetRawProviderM3uAsync(
        string profileId,
        string filterId,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var filter = await db.ProfileGroupFilters
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProfileGroupFilterId == filterId && x.ProfileId == profileId, cancellationToken);

        if (filter is null)
            return TypedResults.NotFound();

        var channels = await db.ProviderChannels
            .AsNoTracking()
            .Where(x => x.ProviderGroupId == filter.ProviderGroupId)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        var sb = new System.Text.StringBuilder();
        foreach (var ch in channels)
        {
            var tvgId    = ch.TvgId    ?? string.Empty;
            var tvgName  = ch.TvgName  ?? ch.DisplayName;
            var logo     = ch.LogoUrl  ?? string.Empty;
            var group    = ch.GroupTitle ?? string.Empty;
            var active   = ch.Active ? "" : " [INACTIVE]";
            sb.Append($"#EXTINF:-1 tvg-id=\"{tvgId}\" tvg-name=\"{tvgName}\" tvg-logo=\"{logo}\" group-title=\"{group}\",{ch.DisplayName}{active}");
            sb.Append('\n');
            sb.Append(ch.StreamUrl);
            sb.Append('\n');
        }

        return TypedResults.Content(sb.ToString(), "text/plain");
    }

    // -------------------------------------------------------------------------
    // Channel stats
    // -------------------------------------------------------------------------

    private static async Task<Ok<ChannelMappingStatsDto>> GetChannelStatsAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var provider = await db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, cancellationToken);

        if (provider is null)
            return TypedResults.Ok(new ChannelMappingStatsDto());

        var profileLink = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Enabled)
            .OrderBy(x => x.Priority)
            .FirstOrDefaultAsync(cancellationToken);

        if (profileLink is null)
            return TypedResults.Ok(new ChannelMappingStatsDto());

        var profileId = profileLink.ProfileId;

        var groupsIncluded = await db.ProfileGroupFilters
            .AsNoTracking()
            .CountAsync(x => x.ProfileId == profileId && x.Decision == "include", cancellationToken);

        var groupsPending = await db.ProfileGroupFilters
            .AsNoTracking()
            .CountAsync(x => x.ProfileId == profileId && x.Decision == "pending", cancellationToken);

        var activeSnapshot = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId && x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var lastFetchRun = await db.FetchRuns
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Status == "ok")
            .OrderByDescending(x => x.StartedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return TypedResults.Ok(new ChannelMappingStatsDto
        {
            ProfileId = profileId,
            GroupsIncluded = groupsIncluded,
            GroupsPending = groupsPending,
            ChannelsInOutput = activeSnapshot?.ChannelCountPublished ?? 0,
            ChannelsInProvider = lastFetchRun?.ChannelCountSeen,
        });
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GroupFilterDto ToDto(ProfileGroupFilter f) => new()
    {
        ProfileGroupFilterId = f.ProfileGroupFilterId,
        ProviderGroupId = f.ProviderGroupId,
        ProviderGroupRawName = f.ProviderGroup.RawName,
        ProviderGroupActive = f.ProviderGroup.Active,
        ProviderGroupFirstSeen = f.ProviderGroup.FirstSeenUtc,
        ProviderGroupLastSeen = f.ProviderGroup.LastSeenUtc,
        ChannelCount = f.ProviderGroup.ChannelCount,
        Decision = f.Decision,
        ChannelMode = f.ChannelMode,
        OutputName = f.OutputName,
        AutoNumStart = f.AutoNumStart,
        AutoNumEnd = f.AutoNumEnd,
        TrackNewChannels = f.TrackNewChannels,
        SortOverride = f.SortOverride,
    };
}
