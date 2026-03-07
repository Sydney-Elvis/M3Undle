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
        return app;
    }

    private static async Task<Results<Ok<ChannelListResponse>, NotFound>> GetChannelsAsync(
        int page,
        int pageSize,
        string? search,
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

        if (string.IsNullOrEmpty(term))
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
            // Search path: full scan, filter to live + search term, then paginate
            var termUpper = term.ToUpperInvariant();
            var all = new List<ChannelListItemDto>();

            await foreach (var e in ChannelIndexStore.StreamAllAsync(snapshot.ChannelIndexPath, cancellationToken))
            {
                if (LiveClassifier.ClassifyContent(e.StreamUrl) != "live") continue;

                if (e.DisplayName.Contains(termUpper, StringComparison.OrdinalIgnoreCase)
                    || (e.TvgId?.Contains(termUpper, StringComparison.OrdinalIgnoreCase) == true)
                    || (e.GroupTitle?.Contains(termUpper, StringComparison.OrdinalIgnoreCase) == true))
                {
                    all.Add(MapEntry(e));
                }
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
    };
}
