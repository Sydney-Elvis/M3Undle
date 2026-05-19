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
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync(ct);

        var profileProviders = await db.ProfileProviders
            .AsNoTracking()
            .Select(pp => new { pp.ProfileId, pp.ProviderId })
            .ToListAsync(ct);

        var relevantProviderIds = profileProviders.Select(pp => pp.ProviderId).Distinct().ToList();

        // Latest fetch run status per provider, keyed by ProviderId
        var latestRunsByProvider = new Dictionary<string, string>(StringComparer.Ordinal);
        if (relevantProviderIds.Count > 0)
        {
            var recentRuns = await db.FetchRuns
                .AsNoTracking()
                .Where(f => relevantProviderIds.Contains(f.ProviderId))
                .Select(f => new { f.ProviderId, f.StartedUtc, f.Status })
                .OrderByDescending(f => f.StartedUtc)
                .ToListAsync(ct);

            foreach (var run in recentRuns)
                latestRunsByProvider.TryAdd(run.ProviderId, run.Status);
        }

        var profileProviderMap = profileProviders
            .GroupBy(pp => pp.ProfileId)
            .ToDictionary(g => g.Key, g => g.Select(pp => pp.ProviderId).ToList());

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

        DateTime? activeProfileProviderExpiresUtc = null;
        if (activeProfileId is not null)
        {
            activeProfileProviderExpiresUtc = await db.ProfileProviders
                .AsNoTracking()
                .Where(pp => pp.ProfileId == activeProfileId && pp.Enabled)
                .Join(
                    db.Providers.AsNoTracking().Where(p => p.Enabled && p.PlaylistExpiresUtc != null),
                    pp => pp.ProviderId,
                    p => p.ProviderId,
                    (pp, p) => p.PlaylistExpiresUtc)
                .OrderBy(expiresUtc => expiresUtc)
                .FirstOrDefaultAsync(ct);
        }

        int publishedLive = 0, publishedMovie = 0, publishedSeries = 0;
        DateTime? lastPublishedUtc = null;
        string? lastChangeClass = null;

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
                    lastChangeClass = snapshot.ChangeClass;
                }
            }

            var profileProviderIds = profileProviderMap.GetValueOrDefault(profile.ProfileId, []);
            var profileHasFailed = profileProviderIds.Any(pid =>
                latestRunsByProvider.TryGetValue(pid, out var s) && s == "fail");

            if (snapshot is not null && profileHasFailed)
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

        var activeProviderIds = activeProfileId is not null
            ? profileProviderMap.GetValueOrDefault(activeProfileId, [])
            : [];
        var refreshFailed = activeProviderIds.Any(pid =>
            latestRunsByProvider.TryGetValue(pid, out var s) && s == "fail");

        var now = DateTime.UtcNow;
        var expiryThreshold = now.AddDays(30);
        var expiringProviders = await db.Providers
            .AsNoTracking()
            .Where(p => p.PlaylistExpiresUtc != null && p.PlaylistExpiresUtc <= expiryThreshold)
            .OrderBy(p => p.PlaylistExpiresUtc)
            .Select(p => new ExpiringProviderWarning(p.ProviderId, p.Name, p.PlaylistExpiresUtc!.Value))
            .ToListAsync(ct);

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
            LastChangeClass = lastChangeClass,
            ActiveProfileProviderExpiresUtc = activeProfileProviderExpiresUtc,
            ExpiringProviders = expiringProviders,
        };
    }
}
