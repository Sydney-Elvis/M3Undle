using M3Undle.Web.Application;
using M3Undle.Web.Application.Epg;
using M3Undle.Web.Data;
using M3Undle.Web.Security;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Api;

public static class DiagnosticsApiEndpoints
{
    public static IEndpointRouteBuilder MapDiagnosticsApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/diagnostics");
        group.RequireAuthorization(UiAccessPolicy.Name);
        group.WithTags("Diagnostics");

        group.MapGet("/providers", GetProvidersAsync).WithSummary("Get provider diagnostics");
        group.MapGet("/streams", GetStreams).WithSummary("Get stream diagnostics");
        group.MapGet("/streams/{sessionId}/health-evidence", GetStreamHealthEvidenceAsync)
            .WithSummary("Get stream channel health evidence for a session");
        group.MapGet("/lineup", GetLineupAsync).WithSummary("Get lineup diagnostics");
        group.MapGet("/epg", GetEpgAsync).WithSummary("Get EPG diagnostics");

        return app;
    }

    private static async Task<IResult> GetStreamHealthEvidenceAsync(
        string sessionId,
        StreamingRegistry registry,
        IStreamChannelHealthProfileService healthProfileService,
        IOptions<ReconnectOptions> reconnectOptions,
        CancellationToken cancellationToken)
    {
        var session = registry.TryGetSession(sessionId);
        if (session is null)
            return TypedResults.NotFound();

        var evidence = await healthProfileService.GetEvidenceAsync(
            session.ProviderId,
            session.ProviderChannelId,
            reconnectOptions.Value,
            cancellationToken);

        return Results.Json(evidence);
    }

    private static async Task<IResult> GetProvidersAsync(
        ApplicationDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var providers = await db.Providers.AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(p => new
            {
                p.ProviderId,
                p.Name,
                p.Enabled,
                p.MaxConcurrentStreams,
                p.PlaylistExpiresUtc,
                latestFetch = db.FetchRuns
                    .Where(r => r.ProviderId == p.ProviderId)
                    .OrderByDescending(r => r.StartedUtc)
                    .Select(r => new
                    {
                        r.FetchRunId,
                        r.Type,
                        r.Status,
                        r.StartedUtc,
                        r.FinishedUtc,
                        r.ChannelCountSeen,
                        r.ErrorSummary,
                    })
                    .FirstOrDefault(),
                activeChannelCount = db.ProviderChannels.Count(c => c.ProviderId == p.ProviderId && c.Active),
            })
            .ToArrayAsync(cancellationToken);

        return Results.Json(new { generatedUtc = timeProvider.GetUtcNow(), providers });
    }

    private static IResult GetStreams(StreamingRegistry registry, TimeProvider timeProvider)
        => Results.Json(new
        {
            generatedUtc = timeProvider.GetUtcNow(),
            activeSessions = registry.GetActiveSessions(),
            recentEndedSessions = registry.GetRecentEndedSessions(),
            activeClients = registry.GetActiveClients(),
            activeProviderStreams = registry.GetActiveProviderStreams(),
        });

    private static async Task<IResult> GetLineupAsync(
        LineupStatusService lineupStatusService,
        ApplicationDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var status = await lineupStatusService.GetStatusAsync(cancellationToken);
        var snapshots = await db.Snapshots.AsNoTracking()
            .OrderByDescending(x => x.CreatedUtc)
            .Take(10)
            .Select(x => new
            {
                x.SnapshotId,
                x.ProfileId,
                x.Status,
                x.CreatedUtc,
                x.ChannelCountPublished,
                x.LiveChannelCount,
                x.VodChannelCount,
                x.SeriesChannelCount,
                x.ChangeClass,
                x.ErrorSummary,
            })
            .ToArrayAsync(cancellationToken);

        return Results.Json(new { generatedUtc = timeProvider.GetUtcNow(), status, snapshots });
    }

    private static async Task<IResult> GetEpgAsync(
        ApplicationDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var sources = await db.EpgSources.AsNoTracking()
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Name)
            .Select(x => new
            {
                x.EpgSourceId,
                x.ProviderId,
                x.Name,
                x.Kind,
                x.Enabled,
                x.Priority,
                x.RefreshIntervalHours,
                channels = db.EpgSourceChannels.Count(c => c.EpgSourceId == x.EpgSourceId),
                latestFetch = db.EpgFetchRuns
                    .Where(r => r.EpgSourceId == x.EpgSourceId)
                    .OrderByDescending(r => r.StartedUtc)
                    .Select(r => new
                    {
                        r.EpgFetchRunId,
                        r.Status,
                        r.StartedUtc,
                        r.FinishedUtc,
                        r.Bytes,
                        r.ErrorSummary,
                    })
                    .FirstOrDefault(),
            })
            .ToArrayAsync(cancellationToken);

        var mappingCount = await db.EpgChannelMappings.AsNoTracking().CountAsync(cancellationToken);
        return Results.Json(new { generatedUtc = timeProvider.GetUtcNow(), mappingCount, sources });
    }
}
