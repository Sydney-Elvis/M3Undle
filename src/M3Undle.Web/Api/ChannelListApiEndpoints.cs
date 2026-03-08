using M3Undle.Core.M3u;
using M3Undle.Web.Application;
using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Api;

public static class ChannelListApiEndpoints
{
    public static IEndpointRouteBuilder MapChannelListApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/channels", GetChannelsAsync);
        app.MapGet("/api/v1/channels/groups", GetMappedGroupsAsync);
        app.MapPatch("/api/v1/channels/{providerChannelId}", UpdateOutputChannelAsync);
        app.MapDelete("/api/v1/channels/{providerChannelId}", RemoveOutputChannelAsync);
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

        var snapshot = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.ProfileId == profileLink.ProfileId && x.Status == "active")
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
            .Where(x => x.ProfileId == profileId && x.Decision != "exclude" && x.ChannelFilters.Any())
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

    private static async Task<string?> GetActiveProfileIdAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
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
