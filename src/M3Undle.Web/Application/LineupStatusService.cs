using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

internal static class LineupStatusCodes
{
    public const string Ok = "ok";
    public const string Refreshing = "refreshing";
    public const string Switching = "switching";
    public const string Degraded = "degraded";
    public const string NoActiveSnapshot = "no_active_snapshot";
    public const string NoActiveProfile = "no_active_profile";
}

internal static class LineupSwitchStates
{
    public const string None = "none";
    public const string Requested = "requested";
    public const string InProgress = "in_progress";
    public const string Complete = "complete";
    public const string Failed = "failed";
}

internal sealed class LineupStatusService(
    IServiceScopeFactory scopeFactory,
    IRefreshTrigger refreshTrigger)
{
    public async Task<LineupStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var isRefreshing = refreshTrigger.IsRefreshing;
        var activeProfile = await db.Profiles
            .AsNoTracking()
            .Where(x => x.IsActive && x.Enabled)
            .OrderByDescending(x => x.UpdatedUtc)
            .Select(x => new ActiveProfileInfo(x.ProfileId, x.Name, x.UpdatedUtc))
            .FirstOrDefaultAsync(cancellationToken);

        var activeProfileId = activeProfile?.ProfileId;

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

        var activeSnapshot = activeProfileId is null
            ? null
            : await db.Snapshots
                .AsNoTracking()
                .Where(x => x.ProfileId == activeProfileId && x.Status == "active")
                .OrderByDescending(x => x.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken);

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

        var activeProviderInfo = activeProvider is null
            ? null
            : new ActiveProviderInfo(activeProvider.ProviderId, activeProvider.Name);

        var switchState = ComputeSwitchState(activeProfile, activeProviderInfo, activeSnapshot, lastRefresh, isRefreshing);
        var lineupStatus = ComputeLineupStatus(activeProfile, activeSnapshot, lastRefresh, isRefreshing);

        var lineup = new LineupStatusInfo(
            Name: "m3undle",
            Status: lineupStatus,
            SwitchState: switchState,
            IsRefreshing: isRefreshing,
            ActiveProfile: activeProfile,
            ActiveProvider: activeProviderInfo,
            ActiveSnapshot: activeSnapshot is null ? null : new ActiveSnapshotInfo(
                activeSnapshot.SnapshotId,
                activeSnapshot.ProfileId,
                activeSnapshot.CreatedUtc,
                activeSnapshot.ChannelCountPublished),
            LastRefresh: lastRefresh);

        return new LineupStatusResponse(lineupStatus, [lineup], isRefreshing);
    }

    private static string ComputeLineupStatus(
        ActiveProfileInfo? activeProfile,
        Snapshot? activeSnapshot,
        LineupFetchRunInfo? lastRefresh,
        bool isRefreshing)
    {
        if (activeProfile is null)
            return LineupStatusCodes.NoActiveProfile;

        if (isRefreshing && activeSnapshot is null)
            return LineupStatusCodes.Switching;

        if (isRefreshing)
            return LineupStatusCodes.Refreshing;

        if (activeSnapshot is null)
            return LineupStatusCodes.NoActiveSnapshot;

        if (lastRefresh?.Status == "fail")
            return LineupStatusCodes.Degraded;

        return LineupStatusCodes.Ok;
    }

    private static string ComputeSwitchState(
        ActiveProfileInfo? activeProfile,
        ActiveProviderInfo? activeProvider,
        Snapshot? activeSnapshot,
        LineupFetchRunInfo? lastRefresh,
        bool isRefreshing)
    {
        if (activeProfile is null)
            return LineupSwitchStates.None;

        if (activeSnapshot is not null)
            return LineupSwitchStates.Complete;

        if (activeProvider is null)
            return LineupSwitchStates.None;

        if (isRefreshing)
            return LineupSwitchStates.InProgress;

        var hasPostActivationAttempt = lastRefresh is not null
            && lastRefresh.StartedUtc >= activeProfile.UpdatedUtc;

        if (hasPostActivationAttempt && lastRefresh?.Status == "fail")
            return LineupSwitchStates.Failed;

        return LineupSwitchStates.Requested;
    }
}

internal sealed record LineupStatusResponse(
    string Status,
    IReadOnlyList<LineupStatusInfo> Lineups,
    bool IsRefreshing = false)
{
    public LineupStatusInfo? Lineup => Lineups.FirstOrDefault(l => l.Name == "m3undle") ?? Lineups.FirstOrDefault();
}

internal sealed record LineupStatusInfo(
    string Name,
    string Status,
    string SwitchState,
    bool IsRefreshing,
    ActiveProfileInfo? ActiveProfile,
    ActiveProviderInfo? ActiveProvider,
    ActiveSnapshotInfo? ActiveSnapshot,
    LineupFetchRunInfo? LastRefresh);

internal sealed record ActiveProfileInfo(string ProfileId, string Name, DateTime UpdatedUtc);

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
