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
            .CountAsync(x => x.ProviderGroup.ContentType == "live" && x.IsNew, ct);

        // Counts shown next to the Output URLs reflect only the active provider's linked profile —
        // that's what the compatibility endpoints actually serve.
        var activeProvider = await db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, ct);

        string? activeProfileId = null;
        if (activeProvider is not null)
        {
            var activeLink = await db.ProfileProviders
                .AsNoTracking()
                .Where(x => x.ProviderId == activeProvider.ProviderId && x.Enabled)
                .OrderBy(x => x.Priority)
                .FirstOrDefaultAsync(ct);
            activeProfileId = activeLink?.ProfileId;
        }

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
                IsActive = profile.ProfileId == activeProfileId,
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
            ChannelsPendingReview = 0,
            GroupsPendingReview = groupsPendingReview,
            ProfileSummaries = summaries,
            LastPublishedUtc = lastPublishedUtc,
            RefreshFailed = refreshFailed,
        };
    }
}
