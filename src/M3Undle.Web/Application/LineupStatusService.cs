using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

internal sealed class LineupStatusService(IServiceScopeFactory scopeFactory)
{
    public async Task<LineupStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var activeSnapshot = await db.Snapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Status == "active", cancellationToken);

        var activeProfileId = await db.Profiles
            .AsNoTracking()
            .Where(x => x.IsActive)
            .Select(x => x.ProfileId)
            .FirstOrDefaultAsync(cancellationToken);

        Provider? activeProvider = null;
        if (activeProfileId is not null)
        {
            var profileProvider = await db.ProfileProviders
                .AsNoTracking()
                .Where(x => x.ProfileId == activeProfileId && x.Enabled)
                .OrderBy(x => x.Priority)
                .FirstOrDefaultAsync(cancellationToken);

            if (profileProvider is not null)
            {
                activeProvider = await db.Providers
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        x => x.ProviderId == profileProvider.ProviderId && x.Enabled,
                        cancellationToken);
            }
        }

        LineupFetchRunInfo? lastRefresh = null;
        if (activeProvider is not null)
        {
            var run = await db.FetchRuns
                .AsNoTracking()
                .Where(x => x.ProviderId == activeProvider.ProviderId && x.Type == "snapshot")
                .OrderByDescending(x => x.StartedUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (run is not null)
            {
                lastRefresh = new LineupFetchRunInfo(
                    run.Status,
                    run.StartedUtc,
                    run.FinishedUtc,
                    run.ChannelCountSeen,
                    run.ErrorSummary);
            }
        }

        var lineupStatus = activeSnapshot is not null
            ? (lastRefresh?.Status == "fail" ? "degraded" : "ok")
            : "no_active_snapshot";

        var lineup = new LineupStatusInfo(
            Name: "m3undle",
            Status: lineupStatus,
            ActiveProvider: activeProvider is null ? null : new ActiveProviderInfo(activeProvider.ProviderId, activeProvider.Name),
            ActiveSnapshot: activeSnapshot is null ? null : new ActiveSnapshotInfo(
                activeSnapshot.SnapshotId,
                activeSnapshot.ProfileId,
                activeSnapshot.CreatedUtc,
                activeSnapshot.ChannelCountPublished),
            LastRefresh: lastRefresh);

        return new LineupStatusResponse(lineupStatus, [lineup]);
    }
}

internal sealed record LineupStatusResponse(
    string Status,
    IReadOnlyList<LineupStatusInfo> Lineups)
{
    public LineupStatusInfo? Lineup => Lineups.FirstOrDefault(l => l.Name == "m3undle") ?? Lineups.FirstOrDefault();
}

internal sealed record LineupStatusInfo(
    string Name,
    string Status,
    ActiveProviderInfo? ActiveProvider,
    ActiveSnapshotInfo? ActiveSnapshot,
    LineupFetchRunInfo? LastRefresh);

internal sealed record ActiveProviderInfo(string ProviderId, string Name);

internal sealed record ActiveSnapshotInfo(
    string SnapshotId,
    string ProfileId,
    DateTime CreatedUtc,
    int ChannelCountPublished);

internal sealed record LineupFetchRunInfo(
    string Status,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    int? ChannelCountSeen,
    string? ErrorSummary);
