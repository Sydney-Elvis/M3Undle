using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

internal sealed class DashboardStatsService(IServiceScopeFactory scopeFactory)
{
    public async Task<DashboardStatsDto> GetStatsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profiles = await db.Profiles
            .AsNoTracking()
            .ToListAsync(ct);

        var activeSnapshots = await db.Snapshots
            .AsNoTracking()
            .Where(s => s.Status == "active")
            .ToListAsync(ct);

        var latestFetchRun = await db.FetchRuns
            .AsNoTracking()
            .OrderByDescending(f => f.StartedUtc)
            .FirstOrDefaultAsync(ct);

        var groupsPendingReview = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .CountAsync(x => x.ProviderGroup.ContentType == "live"
                             && x.IsNew
                             && x.TrackNewChannels, ct);

        var channelsPendingReview = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Include(x => x.ProfileGroupFilter).ThenInclude(f => f.ProviderGroup)
            .Include(x => x.ProviderChannel)
            .CountAsync(x => x.ProfileGroupFilter.ProviderGroup.ContentType == "live"
                             && x.ProfileGroupFilter.TrackingPolicy == LineupReviewSemantics.TrackingPolicyReview
                             && x.ProviderChannel.ContentType == "live"
                             && x.ProviderChannel.Active
                             && !x.ProviderChannel.IsPlaceholder
                             && x.State == LineupReviewSemantics.ChannelStatePending
                             && x.ProfileGroupFilter.TrackNewChannels, ct);

        // Counts shown next to the Output URLs reflect only the active profile's snapshot.
        var activeProfileId = await db.Profiles
            .AsNoTracking()
            .Where(x => x.IsActive)
            .Select(x => (string?)x.ProfileId)
            .FirstOrDefaultAsync(ct);

        int publishedLive = 0, publishedMovie = 0, publishedSeries = 0;
        DateTime? lastPublishedUtc = null;

        var summaries = new List<DashboardProfileSummary>();

        foreach (var profile in profiles)
        {
            var snapshot = activeSnapshots
                .Where(s => s.ProfileId == profile.ProfileId)
                .OrderByDescending(s => s.CreatedUtc)
                .FirstOrDefault();

            var health = ProfileHealthStatus.NoOutput;
            if (snapshot is not null)
            {
                health = ProfileHealthStatus.Ok;

                // Only count channels served by the active profile for the output URL chips
                if (profile.ProfileId == activeProfileId)
                {
                    publishedLive = snapshot.LiveChannelCount;
                    publishedMovie = snapshot.VodChannelCount;
                    publishedSeries = snapshot.SeriesChannelCount;
                    lastPublishedUtc = snapshot.CreatedUtc;
                }
            }

            if (snapshot is not null && latestFetchRun?.Status == "fail")
                health = ProfileHealthStatus.Degraded;

            summaries.Add(new DashboardProfileSummary
            {
                ProfileId = profile.ProfileId,
                DisplayName = profile.Name,
                OutputName = profile.OutputName,
                IsEnabled = profile.Enabled,
                IsActive = profile.IsActive,
                LastPublishedUtc = snapshot?.CreatedUtc,
                LiveCount = snapshot?.LiveChannelCount ?? 0,
                HealthStatus = health,
            });
        }

        var refreshFailed = latestFetchRun is not null && latestFetchRun.Status == "fail";

        return new DashboardStatsDto
        {
            PublishedLiveCount = publishedLive,
            PublishedMovieCount = publishedMovie,
            PublishedSeriesCount = publishedSeries,
            ChannelsPendingReview = channelsPendingReview,
            GroupsPendingReview = groupsPendingReview,
            ProfileSummaries = summaries,
            LastPublishedUtc = lastPublishedUtc,
            RefreshFailed = refreshFailed,
        };
    }
}
