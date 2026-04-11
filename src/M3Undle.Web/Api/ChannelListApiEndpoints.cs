using M3Undle.Core.M3u;
using M3Undle.Web.Application;
using M3Undle.Web.Security;
using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Api;

public static class ChannelListApiEndpoints
{
    public static IEndpointRouteBuilder MapChannelListApiEndpoints(this IEndpointRouteBuilder app)
    {
        var channels = app.MapGroup("/api/v1/channels");
        channels.RequireAuthorization(UiAccessPolicy.Name);
        channels.WithTags("Channels");

        channels.MapGet("/", GetChannelsAsync).WithSummary("List channels");
        channels.MapGet("/groups", GetMappedGroupsAsync).WithSummary("List mapped channel groups");
        channels.MapGet("/number-manager", GetNumberManagerChannelsAsync).WithSummary("List channels for number manager");
        channels.MapPut("/number-manager", BulkUpdateChannelNumbersAsync).WithSummary("Bulk update channel numbers");
        channels.MapPatch("/{providerChannelId}", UpdateOutputChannelAsync).WithSummary("Update output channel overrides");
        channels.MapDelete("/{providerChannelId}", RemoveOutputChannelAsync).WithSummary("Remove a channel from output");

        var profileChannels = app.MapGroup("/api/v1/profiles");
        profileChannels.RequireAuthorization(UiAccessPolicy.Name);
        profileChannels.WithTags("Channels");
        profileChannels.MapGet("/{profileId}/channels", GetProfileChannelsAsync).WithSummary("List channels for a specific profile");

        return app;
    }

    private static async Task<Results<Ok<ChannelListResponse>, NotFound>> GetChannelsAsync(
        int page,
        int pageSize,
        string? search,
        string? group,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        pageSize = Math.Clamp(pageSize, 10, 200);
        page = Math.Max(1, page);

        var activeProfileId = await GetActiveProfileIdAsync(db, cancellationToken);
        if (activeProfileId is null)
            return TypedResults.NotFound();

        var snapshot = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.ProfileId == activeProfileId && x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot is null
            || string.IsNullOrEmpty(snapshot.ChannelIndexPath)
            || !File.Exists(snapshot.ChannelIndexPath))
            return TypedResults.NotFound();

        var term = search?.Trim();
        var groupFilter = group?.Trim();
        bool hasFilters = !string.IsNullOrEmpty(term) || !string.IsNullOrEmpty(groupFilter);

        if (!hasFilters)
        {
            // Fast path: use pre-computed live count as total; skip to page offset
            int total = snapshot.LiveChannelCount;
            int skip = (page - 1) * pageSize;
            var items = new List<ChannelListItemDto>(pageSize);
            int liveCount = 0;

            await foreach (var e in ChannelIndexStore.StreamAllAsync(snapshot.ChannelIndexPath, cancellationToken))
            {
                if (LiveClassifier.ClassifyContent(e.StreamUrl) != "live") continue;
                liveCount++;
                if (liveCount <= skip) continue;
                items.Add(MapEntry(e));
                if (items.Count >= pageSize) break;
            }

            return TypedResults.Ok(new ChannelListResponse
            {
                Total = total,
                Page = page,
                PageSize = pageSize,
                Items = items,
            });
        }
        else
        {
            // Filter path: full scan, apply group + search filters, then paginate
            var termUpper = term?.ToUpperInvariant();
            var all = new List<ChannelListItemDto>();

            await foreach (var e in ChannelIndexStore.StreamAllAsync(snapshot.ChannelIndexPath, cancellationToken))
            {
                if (LiveClassifier.ClassifyContent(e.StreamUrl) != "live") continue;

                // Group filter: exact match on GroupTitle
                if (!string.IsNullOrEmpty(groupFilter)
                    && !string.Equals(e.GroupTitle, groupFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Search filter: substring match on name, tvg-id, or group
                if (!string.IsNullOrEmpty(termUpper)
                    && !e.DisplayName.Contains(termUpper, StringComparison.OrdinalIgnoreCase)
                    && !(e.TvgId?.Contains(termUpper, StringComparison.OrdinalIgnoreCase) == true)
                    && !(e.GroupTitle?.Contains(termUpper, StringComparison.OrdinalIgnoreCase) == true))
                    continue;

                all.Add(MapEntry(e));
            }

            var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            return TypedResults.Ok(new ChannelListResponse
            {
                Total = all.Count,
                Page = page,
                PageSize = pageSize,
                Items = items,
            });
        }
    }

    private static async Task<Results<Ok<ChannelListResponse>, NotFound>> GetProfileChannelsAsync(
        string profileId,
        int page,
        int pageSize,
        string? search,
        string? group,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        pageSize = Math.Clamp(pageSize, 10, 200);
        page = Math.Max(1, page);

        var exists = await db.Profiles.AnyAsync(x => x.ProfileId == profileId, cancellationToken);
        if (!exists)
            return TypedResults.NotFound();

        var snapshot = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId && x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot is null
            || string.IsNullOrEmpty(snapshot.ChannelIndexPath)
            || !File.Exists(snapshot.ChannelIndexPath))
            return TypedResults.NotFound();

        var term = search?.Trim();
        var groupFilter = group?.Trim();
        bool hasFilters = !string.IsNullOrEmpty(term) || !string.IsNullOrEmpty(groupFilter);

        if (!hasFilters)
        {
            int total = snapshot.LiveChannelCount;
            int skip = (page - 1) * pageSize;
            var items = new List<ChannelListItemDto>(pageSize);
            int liveCount = 0;

            await foreach (var e in ChannelIndexStore.StreamAllAsync(snapshot.ChannelIndexPath, cancellationToken))
            {
                if (LiveClassifier.ClassifyContent(e.StreamUrl) != "live") continue;
                liveCount++;
                if (liveCount <= skip) continue;
                items.Add(MapEntry(e));
                if (items.Count >= pageSize) break;
            }

            return TypedResults.Ok(new ChannelListResponse
            {
                Total = total,
                Page = page,
                PageSize = pageSize,
                Items = items,
            });
        }
        else
        {
            var termUpper = term?.ToUpperInvariant();
            var all = new List<ChannelListItemDto>();

            await foreach (var e in ChannelIndexStore.StreamAllAsync(snapshot.ChannelIndexPath, cancellationToken))
            {
                if (LiveClassifier.ClassifyContent(e.StreamUrl) != "live") continue;

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

            var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            return TypedResults.Ok(new ChannelListResponse
            {
                Total = all.Count,
                Page = page,
                PageSize = pageSize,
                Items = items,
            });
        }
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

    // -------------------------------------------------------------------------
    // Mapped groups list (for group filter dropdown on channels page)
    // -------------------------------------------------------------------------

    private static async Task<Results<Ok<List<string>>, NotFound>> GetMappedGroupsAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var profileId = await GetActiveProfileIdAsync(db, cancellationToken);
        if (profileId is null)
            return TypedResults.NotFound();

        var groups = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .Where(x => x.ProfileId == profileId
                        && x.Decision != LineupReviewSemantics.GroupDecisionExclude
                        && x.ChannelFilters.Any())
            .Select(x => x.OutputName ?? x.ProviderGroup.RawName)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(groups);
    }

    // -------------------------------------------------------------------------
    // Update individual channel (channel number / output group override)
    // -------------------------------------------------------------------------

    private static async Task<Results<Ok, NotFound>> UpdateOutputChannelAsync(
        string providerChannelId,
        UpdateOutputChannelRequest request,
        ApplicationDbContext db,
        AppEventBus eventBus,
        CancellationToken cancellationToken)
    {
        var profileId = await GetActiveProfileIdAsync(db, cancellationToken);
        if (profileId is null)
            return TypedResults.NotFound();

        var channelFilter = await db.ProfileGroupChannelFilters
            .Include(x => x.ProfileGroupFilter)
            .FirstOrDefaultAsync(
                x => x.ProviderChannelId == providerChannelId
                     && x.ProfileGroupFilter.ProfileId == profileId,
                cancellationToken);

        if (channelFilter is null)
            return TypedResults.NotFound();

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

        return TypedResults.Ok();
    }

    // -------------------------------------------------------------------------
    // Number Manager — get all channel DB numbers + bulk update
    // -------------------------------------------------------------------------

    private static async Task<Results<Ok<List<NumberManagerChannelDto>>, NotFound>> GetNumberManagerChannelsAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var profileId = await GetActiveProfileIdAsync(db, cancellationToken);
        if (profileId is null)
            return TypedResults.NotFound();

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

        return TypedResults.Ok(rows);
    }

    private static async Task<Results<Ok, NotFound>> BulkUpdateChannelNumbersAsync(
        BulkChannelNumbersRequest request,
        ApplicationDbContext db,
        AppEventBus eventBus,
        CancellationToken cancellationToken)
    {
        var profileId = await GetActiveProfileIdAsync(db, cancellationToken);
        if (profileId is null)
            return TypedResults.NotFound();

        if (request.Channels is not { Count: > 0 })
            return TypedResults.Ok();

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

        return TypedResults.Ok();
    }

    // -------------------------------------------------------------------------
    // Remove channel from output
    // -------------------------------------------------------------------------

    private static async Task<Results<Ok, NotFound>> RemoveOutputChannelAsync(
        string providerChannelId,
        ApplicationDbContext db,
        AppEventBus eventBus,
        CancellationToken cancellationToken)
    {
        var profileId = await GetActiveProfileIdAsync(db, cancellationToken);
        if (profileId is null)
            return TypedResults.NotFound();

        var channelFilter = await db.ProfileGroupChannelFilters
            .Include(x => x.ProfileGroupFilter)
            .FirstOrDefaultAsync(
                x => x.ProviderChannelId == providerChannelId
                     && x.ProfileGroupFilter.ProfileId == profileId,
                cancellationToken);

        if (channelFilter is null)
            return TypedResults.NotFound();

        db.ProfileGroupChannelFilters.Remove(channelFilter);
        await db.SaveChangesAsync(cancellationToken);
        eventBus.Publish(AppEventKind.GroupFiltersChanged);

        return TypedResults.Ok();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Task<string?> GetActiveProfileIdAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        return db.Profiles
            .AsNoTracking()
            .Where(x => x.IsActive)
            .Select(x => (string?)x.ProfileId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
