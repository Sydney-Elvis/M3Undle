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

        var profile = await db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, cancellationToken);

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
            .Where(x => x.ProfileId == profileId)
            .ToListAsync(cancellationToken);

        var selectedCounts = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Where(x => x.ProfileGroupFilter.ProfileId == profileId
                        && x.State == LineupReviewSemantics.ChannelStateIncluded)
            .GroupBy(x => x.ProfileGroupFilterId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        return filters
            .OrderByDescending(f => f.IsNew)
            .ThenBy(f => f.ProviderGroup.RawName, StringComparer.OrdinalIgnoreCase)
            .Select(f => ToDto(f, selectedCounts.GetValueOrDefault(f.ProfileGroupFilterId, 0)))
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
            var normalized = NormalizeRequestedDecision(request.Decision);
            if (normalized is not null)
            {
                filter.Decision = normalized;
            }
        }

        if (request.ChannelMode is not null)
            filter.ChannelMode = LineupReviewSemantics.NormalizeGroupMode(request.ChannelMode);

        if (request.TrackingPolicy is not null)
            filter.TrackingPolicy = LineupReviewSemantics.NormalizeTrackingPolicy(request.TrackingPolicy);

        if (request.ClearTrackingKeywords)
            filter.TrackingKeywords = null;
        else if (request.TrackingKeywords is not null)
            filter.TrackingKeywords = string.IsNullOrWhiteSpace(request.TrackingKeywords) ? null : request.TrackingKeywords.Trim();

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

        if (ShouldClearIsNew(request))
            filter.IsNew = false;

        filter.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (request.Decision == "exclude")
        {
            await db.ProviderChannels
                .Where(x => x.ProviderGroupId == filter.ProviderGroupId && x.ContentType == "live")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false), cancellationToken);
        }

        if (request.Decision is not null || request.ChannelMode is not null || request.TrackingPolicy is not null)
            eventBus.Publish(AppEventKind.GroupFiltersChanged);

        return true;
    }

    public async Task<int> DismissNewGroupsAsync(string profileId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ProfileGroupFilters
            .Where(x => x.ProfileId == profileId && x.IsNew)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsNew, false)
                .SetProperty(x => x.UpdatedUtc, DateTime.UtcNow), cancellationToken);
    }

    public async Task<int> BulkDecideAsync(
        string profileId,
        string decision,
        List<string> providerGroupIds,
        CancellationToken cancellationToken)
    {
        var normalizedDecision = NormalizeRequestedDecision(decision);
        if (normalizedDecision is null)
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
                filter.Decision = normalizedDecision;
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
                    Decision = normalizedDecision,
                    IsNew = false,
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
            var state = LineupReviewSemantics.NormalizeChannelState(sel?.State);
            return new ProviderChannelSelectDto
            {
                ProviderChannelId = ch.ProviderChannelId,
                DisplayName = ch.DisplayName,
                TvgId = ch.TvgId,
                GroupTitle = ch.GroupTitle,
                EventContentKey = ch.EventContentKey,
                Active = ch.Active,
                IsSelected = sel is not null && state == LineupReviewSemantics.ChannelStateIncluded,
                State = state,
                DisplayNameOverride = sel?.DisplayNameOverride,
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
        var now = DateTime.UtcNow;

        var requestedItems = request.Channels
            .Where(x => !string.IsNullOrWhiteSpace(x.ProviderChannelId))
            .GroupBy(x => x.ProviderChannelId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        var existingRows = await db.ProfileGroupChannelFilters
            .Where(x => x.ProfileGroupFilterId == filterId)
            .ToListAsync(cancellationToken);

        foreach (var row in existingRows)
        {
            if (requestedItems.TryGetValue(row.ProviderChannelId, out var item))
            {
                ApplySelectionItem(row, item, now);
                requestedItems.Remove(row.ProviderChannelId);
                continue;
            }

            // Preserve review queue state rows when the inline manual-selection UI saves.
            var state = LineupReviewSemantics.NormalizeChannelState(row.State);
            var preserveManualReviewState = filter.ChannelMode == LineupReviewSemantics.GroupModeManualReview
                && (state == LineupReviewSemantics.ChannelStatePending
                    || state == LineupReviewSemantics.ChannelStateExcluded);

            if (!preserveManualReviewState)
                db.ProfileGroupChannelFilters.Remove(row);
        }

        foreach (var item in requestedItems.Values)
        {
            var row = new ProfileGroupChannelFilter
            {
                ProfileGroupChannelFilterId = Guid.NewGuid().ToString(),
                ProfileGroupFilterId = filterId,
                ProviderChannelId = item.ProviderChannelId,
                CreatedUtc = now,
                UpdatedUtc = now,
            };

            ApplySelectionItem(row, item, now);
            db.ProfileGroupChannelFilters.Add(row);
        }

        filter.IsNew = false;
        filter.UpdatedUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        eventBus.Publish(AppEventKind.GroupFiltersChanged);
        return true;
    }

    private static void ApplySelectionItem(ProfileGroupChannelFilter row, ChannelSelectionItem item, DateTime updatedUtc)
    {
        row.State = LineupReviewSemantics.NormalizeChannelState(item.State);
        row.DisplayNameOverride = string.IsNullOrWhiteSpace(item.DisplayNameOverride) ? null : item.DisplayNameOverride.Trim();
        row.OutputGroupName = string.IsNullOrWhiteSpace(item.OutputGroupName) ? null : item.OutputGroupName.Trim();
        row.ChannelNumber = item.ChannelNumber;
        row.UpdatedUtc = updatedUtc;
    }

    private static GroupFilterDto ToDto(ProfileGroupFilter f, int selectedCount)
    {
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
            Decision = LineupReviewSemantics.NormalizeGroupDecision(f.Decision),
            IsNew = f.IsNew,
            OutputName = f.OutputName,
            AutoNumStart = f.AutoNumStart,
            AutoNumEnd = f.AutoNumEnd,
            ChannelMode = f.ChannelMode,
            TrackingPolicy = LineupReviewSemantics.NormalizeTrackingPolicy(f.TrackingPolicy),
            TrackingKeywords = f.TrackingKeywords,
            TrackNewChannels = f.TrackNewChannels,
            SortOverride = f.SortOverride,
            SelectedChannelCount = selectedCount,
            ChannelCount = f.ProviderGroup.ChannelCount,
        };
    }

    public async Task<List<MappedChannelPanelItem>> GetMappedChannelsPanelAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var snapshotCreatedUtc = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId && x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .Select(x => (DateTime?)x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var rows = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Where(x => x.ProfileGroupFilter.ProfileId == profileId
                        && x.ProfileGroupFilter.Decision == LineupReviewSemantics.GroupDecisionInclude
                        && x.State == LineupReviewSemantics.ChannelStateIncluded
                        && x.ChannelNumber != null)
            .Select(x => new
            {
                x.ChannelNumber,
                DisplayName = x.DisplayNameOverride ?? x.ProviderChannel.DisplayName,
                x.UpdatedUtc,
                FilterId = x.ProfileGroupFilterId,
                OutputName = x.ProfileGroupFilter.OutputName,
            })
            .OrderBy(x => x.ChannelNumber)
            .ToListAsync(cancellationToken);

        var cutoff = snapshotCreatedUtc;
        return rows.Select(r => new MappedChannelPanelItem
        {
            ChannelNumber = r.ChannelNumber,
            DisplayName = r.DisplayName,
            IsLive = cutoff.HasValue && r.UpdatedUtc <= cutoff.Value,
            FilterId = r.FilterId,
            OutputName = r.OutputName,
        }).ToList();
    }

    private static string? NormalizeRequestedDecision(string decision)
    {
        if (string.IsNullOrWhiteSpace(decision))
            return null;

        var value = decision.Trim().ToLowerInvariant();
        return value switch
        {
            LineupReviewSemantics.GroupDecisionExclude => LineupReviewSemantics.GroupDecisionExclude,
            LineupReviewSemantics.GroupDecisionInclude => LineupReviewSemantics.GroupDecisionInclude,
            LineupReviewSemantics.GroupDecisionLegacyPending => LineupReviewSemantics.GroupDecisionInclude,
            LineupReviewSemantics.GroupDecisionLegacyHold => LineupReviewSemantics.GroupDecisionInclude,
            _ => null,
        };
    }

    private static bool ShouldClearIsNew(UpdateGroupFilterRequest request)
        => request.ClearIsNew
           || request.Decision is not null
           || request.ChannelMode is not null
           || request.TrackingPolicy is not null
           || request.TrackingKeywords is not null
           || request.ClearTrackingKeywords
           || request.OutputName is not null
           || request.ClearOutputName
           || request.AutoNumStart is not null
           || request.ClearAutoNum
           || request.AutoNumEnd is not null
           || request.ClearAutoNumEnd
           || request.TrackNewChannels is not null
           || request.SortOverride is not null;

    public async Task<ReviewQueueListResponse> ListReviewQueueAsync(
        string profileId,
        string? providerGroupId,
        string? q,
        bool notifyOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken,
        bool includePendingSummary = true)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var pendingBase = db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Where(x => x.ProfileGroupFilter.ProfileId == profileId
                        && x.ProfileGroupFilter.TrackingPolicy == LineupReviewSemantics.TrackingPolicyReview
                        && x.ProfileGroupFilter.ProviderGroup.ContentType == "live"
                        && x.ProviderChannel.ContentType == "live"
                        && x.ProviderChannel.Active
                        && !x.ProviderChannel.IsPlaceholder
                        && x.State == LineupReviewSemantics.ChannelStatePending);

        int pendingTotal = 0;
        int pendingNotified = 0;

        if (includePendingSummary)
        {
            var pendingCounts = await pendingBase
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    PendingTotal = g.Count(),
                    PendingNotified = g.Count(x => x.ProfileGroupFilter.TrackNewChannels),
                })
                .FirstOrDefaultAsync(cancellationToken);

            pendingTotal = pendingCounts?.PendingTotal ?? 0;
            pendingNotified = pendingCounts?.PendingNotified ?? 0;
        }

        var query = pendingBase;

        if (!string.IsNullOrWhiteSpace(providerGroupId))
            query = query.Where(x => x.ProfileGroupFilter.ProviderGroupId == providerGroupId.Trim());

        if (notifyOnly)
            query = query.Where(x => x.ProfileGroupFilter.TrackNewChannels);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToUpperInvariant();
            query = query.Where(x => EF.Functions.Like(x.ProviderChannel.DisplayName.ToUpper(), $"%{term}%"));
        }

        var total = includePendingSummary
            ? await query.CountAsync(cancellationToken)
            : 0;

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
                IsEvent = x.ProviderChannel.IsEvent,
                EventContentKey = x.ProviderChannel.EventContentKey,
                EventTitle = x.ProviderChannel.EventTitle,
                EventSport = x.ProviderChannel.EventSport,
                EventLeague = x.ProviderChannel.EventLeague,
            })
            .ToListAsync(cancellationToken);

        return new ReviewQueueListResponse
        {
            Total = includePendingSummary ? total : items.Count,
            PendingTotal = pendingTotal,
            PendingNotified = pendingNotified,
            Items = items,
        };
    }

    public async Task<bool> BulkSetReviewQueueStateAsync(
        string profileId,
        BulkReviewQueueStateRequest request,
        CancellationToken cancellationToken)
    {
        var targetState = NormalizeRequestedChannelState(request.State);
        if (targetState is null)
            return false;

        var channelIds = request.ProviderChannelIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var groupIds = request.ProviderGroupIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (channelIds.Count == 0 && groupIds.Count == 0)
            return true;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

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
            query = query.Where(x => channelIds.Contains(x.ProviderChannelId) || groupIds.Contains(x.ProfileGroupFilter.ProviderGroupId));
        else if (channelIds.Count > 0)
            query = query.Where(x => channelIds.Contains(x.ProviderChannelId));
        else
            query = query.Where(x => groupIds.Contains(x.ProfileGroupFilter.ProviderGroupId));

        var updated = await query.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.State, targetState)
            .SetProperty(x => x.UpdatedUtc, now), cancellationToken);

        if (updated > 0)
            eventBus.Publish(AppEventKind.GroupFiltersChanged);

        return true;
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

    public async Task<WhatsOnThisWeekResponse> GetWhatsOnThisWeekAsync(string profileId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profile = await db.Profiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProfileId == profileId, ct);

        if (profile is null)
            return new WhatsOnThisWeekResponse { ProfileId = profileId, GeneratedUtc = DateTime.UtcNow };

        var filters = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .Where(x => x.ProfileId == profileId
                         && x.ProviderGroup.ContentType == "live"
                         && x.ProviderGroup.Active
                         && (x.TrackingPolicy == LineupReviewSemantics.TrackingPolicyAutoAddAll
                             || x.TrackingPolicy == LineupReviewSemantics.TrackingPolicyAutoAddMatching
                             || x.TrackingPolicy == LineupReviewSemantics.TrackingPolicyAutoAddPopulated)
                         && (x.Decision == LineupReviewSemantics.GroupDecisionInclude
                             || (x.Decision == LineupReviewSemantics.GroupDecisionLegacyHold && !x.IsNew)))
            .ToListAsync(ct);

        var channelNumbers = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Where(x => x.ProfileGroupFilter.ProfileId == profileId && x.ChannelNumber != null)
            .ToDictionaryAsync(x => x.ProviderChannelId, x => x.ChannelNumber!.Value, ct);

        var groups = new List<WhatsOnGroupDto>();

        foreach (var filter in filters)
        {
            var channels = await db.ProviderChannels
                .AsNoTracking()
                .Where(x => x.ProviderGroupId == filter.ProviderGroupId
                             && x.Active
                             && x.ContentType == "live"
                             && !x.IsPlaceholder
                             && x.IsEvent)
                .OrderBy(x => x.DisplayName)
                .ToListAsync(ct);

            if (channels.Count == 0) continue;

            var terms = LineupReviewSemantics.ParseTrackingKeywords(filter.TrackingKeywords);
            var isAutoAll = LineupReviewSemantics.ShouldAutoAddAll(filter.TrackingPolicy);
            var isAutoPopulated = LineupReviewSemantics.ShouldAutoAddPopulated(filter.TrackingPolicy);
            var events = new List<WhatsOnEventDto>();

            foreach (var ch in channels)
            {
                string matchedKeyword;
                if (isAutoAll)
                {
                    matchedKeyword = "(all events)";
                }
                else if (isAutoPopulated)
                {
                    if (string.IsNullOrWhiteSpace(ch.EventContentKey)) continue;
                    matchedKeyword = "(populated)";
                }
                else
                {
                    var matched = terms.FirstOrDefault(t =>
                        (ch.DisplayName?.ToUpperInvariant().Contains(t, StringComparison.Ordinal) ?? false)
                        || (ch.EventContentKey?.ToUpperInvariant().Contains(t, StringComparison.Ordinal) ?? false)
                        || (ch.GroupTitle?.ToUpperInvariant().Contains(t, StringComparison.Ordinal) ?? false));
                    if (matched is null) continue;
                    matchedKeyword = matched;
                }

                channelNumbers.TryGetValue(ch.ProviderChannelId, out var chNum);

                events.Add(new WhatsOnEventDto
                {
                    DisplayName = ch.DisplayName,
                    GroupName = filter.ProviderGroup.RawName,
                    EventContentKey = ch.EventContentKey,
                    EventStartUtc = ch.EventStartUtc,
                    EventEndUtc = ch.EventEndUtc,
                    ChannelNumber = chNum == 0 ? null : chNum,
                    MatchedKeyword = matchedKeyword,
                });
            }

            if (events.Count > 0)
            {
                groups.Add(new WhatsOnGroupDto
                {
                    GroupName = filter.ProviderGroup.RawName,
                    TrackingPolicy = LineupReviewSemantics.NormalizeTrackingPolicy(filter.TrackingPolicy),
                    Events = events,
                });
            }
        }

        // Include custom groups with auto-add tracking policies
        var customGroups = await db.ProfileCustomGroups
            .AsNoTracking()
            .Include(x => x.Channels).ThenInclude(c => c.ProviderChannel)
            .Where(x => x.ProfileId == profileId
                        && x.Decision == LineupReviewSemantics.GroupDecisionInclude
                        && (x.TrackingPolicy == LineupReviewSemantics.TrackingPolicyAutoAddAll
                            || x.TrackingPolicy == LineupReviewSemantics.TrackingPolicyAutoAddMatching
                            || x.TrackingPolicy == LineupReviewSemantics.TrackingPolicyAutoAddPopulated))
            .ToListAsync(ct);

        foreach (var cg in customGroups)
        {
            var eventChannels = cg.Channels
                .Where(c => c.ProviderChannel.Active
                            && c.ProviderChannel.ContentType == "live"
                            && !c.ProviderChannel.IsPlaceholder
                            && c.ProviderChannel.IsEvent)
                .OrderBy(c => c.ProviderChannel.DisplayName)
                .ToList();

            if (eventChannels.Count == 0) continue;

            var cgTerms = LineupReviewSemantics.ParseTrackingKeywords(cg.TrackingKeywords);
            var cgIsAutoAll = LineupReviewSemantics.ShouldAutoAddAll(cg.TrackingPolicy);
            var cgIsAutoPopulated = LineupReviewSemantics.ShouldAutoAddPopulated(cg.TrackingPolicy);
            var cgEvents = new List<WhatsOnEventDto>();

            foreach (var row in eventChannels)
            {
                var ch = row.ProviderChannel;
                string matchedKeyword;
                if (cgIsAutoAll)
                {
                    matchedKeyword = "(all events)";
                }
                else if (cgIsAutoPopulated)
                {
                    if (string.IsNullOrWhiteSpace(ch.EventContentKey)) continue;
                    matchedKeyword = "(populated)";
                }
                else
                {
                    var matched = cgTerms.FirstOrDefault(t =>
                        (ch.DisplayName?.ToUpperInvariant().Contains(t, StringComparison.Ordinal) ?? false)
                        || (ch.EventContentKey?.ToUpperInvariant().Contains(t, StringComparison.Ordinal) ?? false)
                        || (ch.GroupTitle?.ToUpperInvariant().Contains(t, StringComparison.Ordinal) ?? false));
                    if (matched is null) continue;
                    matchedKeyword = matched;
                }

                cgEvents.Add(new WhatsOnEventDto
                {
                    DisplayName = ch.DisplayName,
                    GroupName = cg.Name,
                    EventContentKey = ch.EventContentKey,
                    EventStartUtc = ch.EventStartUtc,
                    EventEndUtc = ch.EventEndUtc,
                    ChannelNumber = row.ChannelNumber == 0 ? null : row.ChannelNumber,
                    MatchedKeyword = matchedKeyword,
                });
            }

            if (cgEvents.Count > 0)
            {
                groups.Add(new WhatsOnGroupDto
                {
                    GroupName = cg.Name + " (custom)",
                    TrackingPolicy = LineupReviewSemantics.NormalizeTrackingPolicy(cg.TrackingPolicy),
                    Events = cgEvents,
                });
            }
        }

        return new WhatsOnThisWeekResponse
        {
            ProfileId = profileId,
            ProfileName = profile.Name,
            GeneratedUtc = DateTime.UtcNow,
            TotalEvents = groups.Sum(g => g.Events.Count),
            Groups = groups,
        };
    }
}
