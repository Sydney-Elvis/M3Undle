using M3Undle.Web.Application;
using M3Undle.Web.Security;
using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace M3Undle.Web.Api;

public static class ChannelFilterApiEndpoints
{
    public static IEndpointRouteBuilder MapChannelFilterApiEndpoints(this IEndpointRouteBuilder app)
    {
        var profiles = app.MapGroup("/api/v1/profiles");
        profiles.RequireAuthorization(UiAccessPolicy.Name);
        profiles.MapGet("/active", GetActiveProfileAsync);
        profiles.MapGet("/{profileId}/group-filters", ListGroupFiltersAsync);
        profiles.MapPatch("/{profileId}/group-filters/{filterId}", UpdateGroupFilterAsync);
        profiles.MapPost("/{profileId}/group-filters/bulk-decide", BulkDecideAsync);
        profiles.MapPost("/{profileId}/group-filters/dismiss-new", DismissNewGroupsAsync);
        profiles.MapGet("/{profileId}/group-filters/{filterId}/channels", GetGroupChannelsAsync);
        profiles.MapGet("/{profileId}/channels/search", SearchChannelsAsync);
        profiles.MapGet("/{profileId}/group-filters/{filterId}/channel-selections", GetChannelSelectionsAsync);
        profiles.MapPut("/{profileId}/group-filters/{filterId}/channel-selections", UpdateChannelSelectionsAsync);
        profiles.MapGet("/{profileId}/review-queue", ListReviewQueueAsync);
        profiles.MapPost("/{profileId}/review-queue/bulk-state", BulkSetReviewQueueStateAsync);

        app.MapGet("/api/v1/channel-stats", GetChannelStatsAsync).RequireAuthorization(UiAccessPolicy.Name);

        return app;
    }

    // -------------------------------------------------------------------------
    // Active profile
    // -------------------------------------------------------------------------

    private static async Task<Results<Ok<ActiveProfileDto>, NotFound>> GetActiveProfileAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var profile = await db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, cancellationToken);

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
            .Include(x => x.ProviderGroup).ThenInclude(g => g.Provider)
            .Include(x => x.ChannelFilters)
            .Where(x => x.ProfileId == profileId)
            .ToListAsync(cancellationToken);

        var dtos = filters
            .OrderBy(f => LineupReviewSemantics.IsGroupPending(f.Decision, f.IsNew) ? 0 : 1)
            .ThenByDescending(f => f.IsNew)
            .ThenBy(f => f.ProviderGroup.RawName, StringComparer.OrdinalIgnoreCase)
            .Select(f => ToDto(f))
            .ToList();

        return TypedResults.Ok(dtos);
    }

    private sealed class ChannelFilterLog { }

    private static async Task<Results<Ok<GroupFilterDto>, NotFound>> UpdateGroupFilterAsync(
        string profileId,
        string filterId,
        UpdateGroupFilterRequest request,
        ApplicationDbContext db,
        AppEventBus eventBus,
        ILogger<ChannelFilterLog> logger,
        CancellationToken cancellationToken)
    {
        var filter = await db.ProfileGroupFilters
            .Include(x => x.ProviderGroup).ThenInclude(g => g.Provider)
            .FirstOrDefaultAsync(x => x.ProfileGroupFilterId == filterId && x.ProfileId == profileId, cancellationToken);

        if (filter is null)
            return TypedResults.NotFound();

        if (request.Decision is not null)
        {
            var normalizedDecision = NormalizeRequestedDecision(request.Decision);
            if (normalizedDecision is not null)
            {
                filter.Decision = normalizedDecision;
                filter.IsNew = normalizedDecision == LineupReviewSemantics.GroupDecisionPending;
            }
        }

        if (request.ChannelMode is not null)
        {
            var normalizedMode = LineupReviewSemantics.NormalizeGroupMode(request.ChannelMode);
            filter.ChannelMode = normalizedMode;
        }

        if (request.TrackingPolicy is not null)
            filter.TrackingPolicy = LineupReviewSemantics.NormalizeTrackingPolicy(request.TrackingPolicy);

        if (request.ClearTrackingKeywords)
            filter.TrackingKeywords = null;
        else if (request.TrackingKeywords is not null)
            filter.TrackingKeywords = string.IsNullOrWhiteSpace(request.TrackingKeywords) ? null : request.TrackingKeywords.Trim();

        if (request.ClearIsNew)
        {
            filter.IsNew = false;
            if (request.Decision is null
                && LineupReviewSemantics.NormalizeGroupDecision(filter.Decision, true) == LineupReviewSemantics.GroupDecisionPending)
            {
                filter.Decision = LineupReviewSemantics.GroupDecisionInclude;
            }
        }

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
            await db.ProviderChannels
                .Where(x => x.ProviderGroupId == filter.ProviderGroupId && x.ContentType == "live")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false), cancellationToken);

        if (request.Decision is not null || request.ChannelMode is not null || request.TrackingPolicy is not null)
        {
            using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Channel" });
            if (request.Decision is not null)
            {
                logger.LogInformation(
                    "Channel group '{GroupName}' set to '{Decision}' for profile {ProfileId}.",
                    filter.ProviderGroup?.RawName ?? filterId,
                    request.Decision,
                    profileId);
            }
            if (request.ChannelMode is not null)
            {
                logger.LogInformation(
                    "Channel group '{GroupName}' mode set to '{Mode}' for profile {ProfileId}.",
                    filter.ProviderGroup?.RawName ?? filterId,
                    filter.ChannelMode,
                    profileId);
            }
            if (request.TrackingPolicy is not null)
            {
                logger.LogInformation(
                    "Channel group '{GroupName}' tracking policy set to '{Policy}' for profile {ProfileId}.",
                    filter.ProviderGroup?.RawName ?? filterId,
                    filter.TrackingPolicy,
                    profileId);
            }
            eventBus.Publish(AppEventKind.GroupFiltersChanged);
        }

        return TypedResults.Ok(ToDto(filter));
    }

    private static async Task<Ok<object>> DismissNewGroupsAsync(
        string profileId,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var updated = await db.ProfileGroupFilters
            .Where(x => x.ProfileId == profileId && x.IsNew)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsNew, false)
                .SetProperty(x => x.Decision, LineupReviewSemantics.GroupDecisionInclude)
                .SetProperty(x => x.UpdatedUtc, DateTime.UtcNow), cancellationToken);

        return TypedResults.Ok((object)new { updated });
    }

    private static async Task<Results<Ok<object>, BadRequest<string>>> BulkDecideAsync(
        string profileId,
        BulkGroupDecisionRequest request,
        ApplicationDbContext db,
        AppEventBus eventBus,
        ILogger<ChannelFilterLog> logger,
        CancellationToken cancellationToken)
    {
        var normalizedDecision = NormalizeRequestedDecision(request.Decision);
        if (normalizedDecision is null)
            return TypedResults.BadRequest("Decision must be 'pending', 'include', or 'exclude'.");

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
                filter.Decision = normalizedDecision;
                filter.IsNew = normalizedDecision == LineupReviewSemantics.GroupDecisionPending;
                filter.UpdatedUtc = now;
            }
            else
            {
                db.ProfileGroupFilters.Add(new ProfileGroupFilter
                {
                    ProfileGroupFilterId = Guid.NewGuid().ToString(),
                    ProfileId = profileId,
                    ProviderGroupId = groupId,
                    Decision = normalizedDecision,
                    IsNew = normalizedDecision == LineupReviewSemantics.GroupDecisionPending,
                    ChannelMode = LineupReviewSemantics.GroupModeManualReview,
                    TrackingPolicy = LineupReviewSemantics.TrackingPolicyReview,
                    TrackNewChannels = false,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                });
            }
            updated++;
        }

        await db.SaveChangesAsync(cancellationToken);

        if (normalizedDecision == LineupReviewSemantics.GroupDecisionExclude)
        {
            var groupIds = request.ProviderGroupIds;
            await db.ProviderChannels
                .Where(x => x.ProviderGroupId != null && groupIds.Contains(x.ProviderGroupId!) && x.ContentType == "live")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false), cancellationToken);
        }

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Channel" });
        logger.LogInformation(
            "Bulk channel group decision: {Count} group(s) set to '{Decision}' for profile {ProfileId}.",
            updated,
            normalizedDecision,
            profileId);
        eventBus.Publish(AppEventKind.GroupFiltersChanged);

        return TypedResults.Ok((object)new { updated });
    }

    // -------------------------------------------------------------------------
    // Global channel search across all groups for a profile
    // -------------------------------------------------------------------------

    private static async Task<Results<Ok<List<ChannelSearchGroupResult>>, BadRequest<string>>> SearchChannelsAsync(
        string profileId,
        string? q,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var term = q?.Trim().Replace("*", "").Replace("!", "").ToUpperInvariant() ?? string.Empty;
        if (term.Length < 2)
            return TypedResults.BadRequest("Search term must be at least 2 characters.");

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

        var grouped = matches
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

        return TypedResults.Ok(grouped);
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

        if (filter.Decision == "exclude")
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

        try
        {
            // Stream line-by-line — no full-file allocation.
            // CancellationToken.None: don't let a client disconnect surface an unhandled exception
            // mid-stream; the OS page cache stays warm for subsequent requests.
            var channels = new List<GroupChannelDto>();
            await foreach (var e in ChannelIndexStore.StreamAllAsync(
                snapshot.ChannelIndexPath, CancellationToken.None))
            {
                if (!string.Equals(e.GroupTitle, outputName, StringComparison.Ordinal)) continue;
                channels.Add(new GroupChannelDto
                {
                    Number = e.TvgChno,
                    DisplayName = e.DisplayName,
                    TvgId = e.TvgId,
                });
            }
            channels.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName));
            return TypedResults.Ok(new GroupChannelsResponse { IsInOutput = true, Channels = channels });
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException)
        {
            return TypedResults.Ok(new GroupChannelsResponse { IsInOutput = true });
        }
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

        var existingSelections = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Where(x => x.ProfileGroupFilterId == filterId)
            .ToListAsync(cancellationToken);

        var selectionByChannelId = existingSelections.ToDictionary(x => x.ProviderChannelId, StringComparer.Ordinal);

        var dtos = allChannels.Select(ch =>
        {
            selectionByChannelId.TryGetValue(ch.ProviderChannelId, out var sel);
            var state = LineupReviewSemantics.NormalizeChannelState(sel?.State);
            return new ProviderChannelSelectDto
            {
                ProviderChannelId = ch.ProviderChannelId,
                DisplayName = ch.DisplayName,
                TvgId = ch.TvgId,
                Active = ch.Active,
                IsSelected = sel is not null && state == LineupReviewSemantics.ChannelStateIncluded,
                State = state,
                DisplayNameOverride = sel?.DisplayNameOverride,
                OutputGroupName = sel?.OutputGroupName,
                ChannelNumber = sel?.ChannelNumber,
            };
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
        EpgPageService epgPageService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var filter = await db.ProfileGroupFilters
            .Include(x => x.ProviderGroup)
            .FirstOrDefaultAsync(x => x.ProfileGroupFilterId == filterId && x.ProfileId == profileId, cancellationToken);

        if (filter is null)
            return TypedResults.NotFound();

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
                db.ProfileGroupChannelFilters.Add(new Data.Entities.ProfileGroupChannelFilter
                {
                    ProfileGroupChannelFilterId = Guid.NewGuid().ToString(),
                    ProfileGroupFilterId = filterId,
                    ProviderChannelId = item.ProviderChannelId,
                    State = LineupReviewSemantics.NormalizeChannelState(item.State),
                    DisplayNameOverride = string.IsNullOrWhiteSpace(item.DisplayNameOverride) ? null : item.DisplayNameOverride.Trim(),
                    OutputGroupName = string.IsNullOrWhiteSpace(item.OutputGroupName) ? null : item.OutputGroupName.Trim(),
                    ChannelNumber = item.ChannelNumber,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var channelLogger = loggerFactory.CreateLogger("ChannelFilterApiEndpoints");
        using var scope = channelLogger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Channel" });

        if (filter.ChannelMode == "select")
        {
            channelLogger.LogInformation(
                "{Count} channel(s) mapped in group '{GroupName}' for profile {ProfileId}.",
                request.Channels.Count,
                filter.ProviderGroup?.RawName ?? filterId,
                profileId);
        }
        else
        {
            channelLogger.LogInformation(
                "Channel group '{GroupName}' set to include all channels for profile {ProfileId}.",
                filter.ProviderGroup?.RawName ?? filterId,
                profileId);
        }

        eventBus.Publish(AppEventKind.GroupFiltersChanged);

        // Fire-and-forget EPG auto-map so newly mapped channels get guide data assigned
        if (filter.ProviderGroup is not null)
        {
            _ = RunAutoMapSafeAsync(
                epgPageService,
                profileId,
                filter.ProviderGroup.ProviderId,
                channelLogger);
        }

        return await GetChannelSelectionsAsync(profileId, filterId, db, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Pending review queue
    // -------------------------------------------------------------------------

    private static async Task<Results<Ok<ReviewQueueListResponse>, NotFound<string>>> ListReviewQueueAsync(
        string profileId,
        ApplicationDbContext db,
        string? providerGroupId,
        string? q,
        bool notifyOnly,
        int page = 1,
        int pageSize = 200,
        CancellationToken cancellationToken = default)
    {
        var exists = await db.Profiles
            .AsNoTracking()
            .AnyAsync(x => x.ProfileId == profileId, cancellationToken);

        if (!exists)
            return TypedResults.NotFound("Profile not found.");

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var pendingBase = db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Include(x => x.ProfileGroupFilter).ThenInclude(f => f.ProviderGroup)
            .Include(x => x.ProviderChannel)
            .Where(x => x.ProfileGroupFilter.ProfileId == profileId
                        && x.ProfileGroupFilter.TrackingPolicy == LineupReviewSemantics.TrackingPolicyReview
                        && x.ProfileGroupFilter.ProviderGroup.ContentType == "live"
                        && x.ProviderChannel.ContentType == "live"
                        && x.ProviderChannel.Active
                        && !x.ProviderChannel.IsPlaceholder
                        && x.State == LineupReviewSemantics.ChannelStatePending);

        var pendingTotal = await pendingBase.CountAsync(cancellationToken);
        var pendingNotified = await pendingBase
            .CountAsync(x => x.ProfileGroupFilter.TrackNewChannels, cancellationToken);

        var query = pendingBase;

        if (!string.IsNullOrWhiteSpace(providerGroupId))
        {
            var trimmedGroupId = providerGroupId.Trim();
            query = query.Where(x => x.ProfileGroupFilter.ProviderGroupId == trimmedGroupId);
        }

        if (notifyOnly)
            query = query.Where(x => x.ProfileGroupFilter.TrackNewChannels);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToUpperInvariant();
            query = query.Where(x => EF.Functions.Like(x.ProviderChannel.DisplayName.ToUpper(), $"%{term}%"));
        }

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(x => x.ProfileGroupFilter.TrackNewChannels)
            .ThenBy(x => x.ProfileGroupFilter.ProviderGroup.RawName)
            .ThenBy(x => x.ProviderChannel.DisplayName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ReviewQueueItemDto
            {
                ProviderChannelId = x.ProviderChannelId,
                ProfileGroupFilterId = x.ProfileGroupFilterId,
                ProviderGroupId = x.ProfileGroupFilter.ProviderGroupId,
                ProviderGroupRawName = x.ProfileGroupFilter.ProviderGroup.RawName,
                DisplayName = x.DisplayNameOverride ?? x.ProviderChannel.DisplayName,
                TvgId = x.TvgIdOverride ?? x.ProviderChannel.TvgId,
                FirstSeenUtc = x.ProviderChannel.FirstSeenUtc,
                LastSeenUtc = x.ProviderChannel.LastSeenUtc,
                Active = x.ProviderChannel.Active,
                NotifyOnPending = x.ProfileGroupFilter.TrackNewChannels,
                State = LineupReviewSemantics.NormalizeChannelState(x.State),
            })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new ReviewQueueListResponse
        {
            Total = total,
            PendingTotal = pendingTotal,
            PendingNotified = pendingNotified,
            Items = items,
        });
    }

    private static async Task<Results<Ok<object>, BadRequest<string>>> BulkSetReviewQueueStateAsync(
        string profileId,
        BulkReviewQueueStateRequest request,
        ApplicationDbContext db,
        AppEventBus eventBus,
        CancellationToken cancellationToken)
    {
        var targetState = NormalizeRequestedChannelState(request.State);
        if (targetState is null)
            return TypedResults.BadRequest("State must be 'included', 'excluded', or 'pending'.");

        var channelIds = request.ProviderChannelIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var groupIds = request.ProviderGroupIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (channelIds.Count == 0 && groupIds.Count == 0)
            return TypedResults.Ok((object)new { updated = 0, state = targetState });

        var now = DateTime.UtcNow;

        var query = db.ProfileGroupChannelFilters
            .Where(x => x.ProfileGroupFilter.ProfileId == profileId
                        && x.ProfileGroupFilter.TrackingPolicy == LineupReviewSemantics.TrackingPolicyReview
                        && x.ProfileGroupFilter.ProviderGroup.ContentType == "live"
                        && x.ProviderChannel.ContentType == "live"
                        && x.ProviderChannel.Active
                        && !x.ProviderChannel.IsPlaceholder
                        && x.State == LineupReviewSemantics.ChannelStatePending);

        if (channelIds.Count > 0 && groupIds.Count > 0)
        {
            query = query.Where(x =>
                channelIds.Contains(x.ProviderChannelId)
                || groupIds.Contains(x.ProfileGroupFilter.ProviderGroupId));
        }
        else if (channelIds.Count > 0)
        {
            query = query.Where(x => channelIds.Contains(x.ProviderChannelId));
        }
        else
        {
            query = query.Where(x => groupIds.Contains(x.ProfileGroupFilter.ProviderGroupId));
        }

        var updated = await query.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.State, targetState)
            .SetProperty(x => x.UpdatedUtc, now), cancellationToken);

        if (updated > 0)
            eventBus.Publish(AppEventKind.GroupFiltersChanged);

        return TypedResults.Ok((object)new { updated, state = targetState });
    }

    private static async Task RunAutoMapSafeAsync(
        EpgPageService epgPageService,
        string profileId,
        string providerId,
        ILogger logger)
    {
        try
        {
            await epgPageService.AutoMapForProviderAsync(profileId, providerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "EPG auto-map failed after channel selection update for profile {ProfileId}, provider {ProviderId}.",
                profileId,
                providerId);
        }
    }

    // -------------------------------------------------------------------------
    // Channel stats
    // -------------------------------------------------------------------------

    private static async Task<Ok<ChannelMappingStatsDto>> GetChannelStatsAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var activeProfile = await db.Profiles
            .AsNoTracking()
            .Include(x => x.ProfileProviders)
                .ThenInclude(pp => pp.Provider)
            .FirstOrDefaultAsync(x => x.IsActive, cancellationToken);

        if (activeProfile is null)
            return TypedResults.Ok(new ChannelMappingStatsDto());

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
                                 || (x.Decision == LineupReviewSemantics.GroupDecisionLegacyHold && !x.IsNew))
                             && x.ChannelFilters.Any(), cancellationToken);

        var groupsHold = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .CountAsync(x => x.ProfileId == profileId
                             && x.ProviderGroup.ContentType == "live"
                             && (x.Decision == LineupReviewSemantics.GroupDecisionPending
                                 || (x.Decision == LineupReviewSemantics.GroupDecisionLegacyHold && x.IsNew))
                             && !x.ChannelFilters.Any(), cancellationToken);

        var groupsNew = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .CountAsync(x => x.ProfileId == profileId
                             && x.ProviderGroup.ContentType == "live"
                             && (x.Decision == LineupReviewSemantics.GroupDecisionPending
                                 || (x.Decision == LineupReviewSemantics.GroupDecisionLegacyHold && x.IsNew))
                             && x.TrackNewChannels, cancellationToken);

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
                             && x.ProfileGroupFilter.ProviderGroup.ContentType == "live", cancellationToken);

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
                             && x.ProfileGroupFilter.TrackNewChannels, cancellationToken);

        var activeSnapshot = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId && x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        int? channelsInProvider = null;
        int vodGroups = 0;
        int seriesGroups = 0;

        if (provider is not null)
        {
            var lastFetchRun = await db.FetchRuns
                .AsNoTracking()
                .Where(x => x.ProviderId == provider.ProviderId && x.Status == "ok")
                .OrderByDescending(x => x.StartedUtc)
                .FirstOrDefaultAsync(cancellationToken);

            channelsInProvider = lastFetchRun?.ChannelCountSeen;

            vodGroups = await db.ProviderGroups
                .AsNoTracking()
                .CountAsync(x => x.ProviderId == provider.ProviderId && x.Active && x.ContentType == "vod", cancellationToken);

            seriesGroups = await db.ProviderGroups
                .AsNoTracking()
                .CountAsync(x => x.ProviderId == provider.ProviderId && x.Active && x.ContentType == "series", cancellationToken);
        }

        return TypedResults.Ok(new ChannelMappingStatsDto
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
        ProviderGroupContentType = f.ProviderGroup.ContentType,
        ChannelCount = f.ProviderGroup.ChannelCount,
        SelectedChannelCount = f.ChannelFilters.Count,
        ProviderName = f.ProviderGroup.Provider.Name,
        Decision = LineupReviewSemantics.NormalizeGroupDecision(f.Decision, f.IsNew),
        IsNew = f.IsNew,
        ChannelMode = f.ChannelMode,
        TrackingPolicy = LineupReviewSemantics.NormalizeTrackingPolicy(f.TrackingPolicy),
        TrackingKeywords = f.TrackingKeywords,
        OutputName = f.OutputName,
        AutoNumStart = f.AutoNumStart,
        AutoNumEnd = f.AutoNumEnd,
        TrackNewChannels = f.TrackNewChannels,
        SortOverride = f.SortOverride,
    };

    private static string? NormalizeRequestedDecision(string decision)
    {
        if (string.IsNullOrWhiteSpace(decision))
            return null;

        var value = decision.Trim().ToLowerInvariant();
        return value switch
        {
            LineupReviewSemantics.GroupDecisionExclude => LineupReviewSemantics.GroupDecisionExclude,
            LineupReviewSemantics.GroupDecisionInclude => LineupReviewSemantics.GroupDecisionInclude,
            LineupReviewSemantics.GroupDecisionPending => LineupReviewSemantics.GroupDecisionPending,
            LineupReviewSemantics.GroupDecisionLegacyHold => LineupReviewSemantics.GroupDecisionInclude,
            _ => null,
        };
    }

    private static string? NormalizeRequestedChannelState(string state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return null;

        var value = state.Trim().ToLowerInvariant();
        return value switch
        {
            LineupReviewSemantics.ChannelStatePending => LineupReviewSemantics.ChannelStatePending,
            LineupReviewSemantics.ChannelStateIncluded => LineupReviewSemantics.ChannelStateIncluded,
            LineupReviewSemantics.ChannelStateExcluded => LineupReviewSemantics.ChannelStateExcluded,
            _ => null,
        };
    }
}
