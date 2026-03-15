using System.Text.Json;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Security;
using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Streaming.Observability;
using M3Undle.Web.Streaming.Resolution;
using M3Undle.Web.Streaming.Sessions;
using M3Undle.Web.Streaming.Upstream;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Api;

public static class CompatibilityEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static IEndpointRouteBuilder MapCompatibilityEndpoints(this IEndpointRouteBuilder app)
    {
        var client = app.MapClientSurface();

        client.MapGet("m3u/m3undle.m3u", ServeM3uAsync);
        client.MapGet("xmltv/m3undle.xml", ServeXmltvAsync);
        client.MapGet("live/{streamKey}", ServeStreamAsync);
        client.MapGet("live/{streamKey}/{*tail}", ServeStreamAsync);
        client.MapGet("movie/{streamKey}", ServeStreamAsync);
        client.MapGet("movie/{streamKey}/{*tail}", ServeStreamAsync);
        client.MapGet("vod/{streamKey}", ServeStreamAsync);
        client.MapGet("vod/{streamKey}/{*tail}", ServeStreamAsync);
        client.MapGet("series/{streamKey}", ServeStreamAsync);
        client.MapGet("series/{streamKey}/{*tail}", ServeStreamAsync);
        client.MapGet("stream/{streamKey}", ServeStreamAsync);
        client.MapGet("tune/{streamKey}", ServeStreamAsync);
        client.MapGet("tune/{streamKey}/{*tail}", ServeStreamAsync);
        client.MapGet("hdhr/tune/{streamKey}", ServeStreamAsync);
        client.MapGet("hdhr/tune/{streamKey}/{*tail}", ServeStreamAsync);

        app.MapGet("/status", ServeStatusAsync).AllowAnonymous();

        var streamStatus = app.MapGroup("/status/streams")
            .RequireAuthorization(UiAccessPolicy.Name);
        streamStatus.MapGet(string.Empty, ServeStreamsStatusSummaryAsync);
        streamStatus.MapGet("clients", ServeStreamsClientsStatusAsync);
        streamStatus.MapGet("providers", ServeStreamsProvidersStatusAsync);
        streamStatus.MapGet("{sessionId}", ServeStreamsSingleSessionStatusAsync);

        return app;
    }

    private static async Task ServeM3uAsync(
        HttpContext context,
        ILineupRenderer lineupRenderer,
        IM3USerializer m3uSerializer,
        CancellationToken cancellationToken)
    {
        try
        {
            var access = context.GetResolvedClientAccess();
            var lineup = await lineupRenderer.TryRenderActiveLineupAsync(access.Binding.ActiveProfileId, cancellationToken);

            if (lineup is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.Append("Retry-After", "60");
                await context.Response.WriteAsync("No active snapshot available. Waiting for first refresh.", cancellationToken);
                return;
            }

            await m3uSerializer.WriteAsync(context, lineup, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.Append("Retry-After", "30");
                await context.Response.WriteAsync("Active snapshot data is unavailable.", cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected before response completed
        }
    }

    private static async Task<IResult> ServeXmltvAsync(
        HttpContext context,
        ILineupRenderer lineupRenderer,
        IXmlTvSerializer xmlTvSerializer,
        CancellationToken cancellationToken)
    {
        try
        {
            var access = context.GetResolvedClientAccess();
            var lineup = await lineupRenderer.TryRenderActiveLineupAsync(access.Binding.ActiveProfileId, cancellationToken);
            if (lineup is null)
            {
                return TypedResults.Problem(
                    "No active snapshot available.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return xmlTvSerializer.Serialize(lineup);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return TypedResults.Problem(
                "Active snapshot data is unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task ServeStreamAsync(
        string streamKey,
        HttpContext context,
        ApplicationDbContext db,
        StreamRequestResolver streamRequestResolver,
        ChannelSessionManager channelSessionManager,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IStreamingSettingsService streamingSettings,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("M3Undle.Stream");
        using var streamScope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Stream" });

        StreamResolveResult resolved;
        try
        {
            resolved = await streamRequestResolver.ResolveAsync(streamKey, context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (!resolved.IsSuccess || resolved.Entry is null)
        {
            context.Response.StatusCode = resolved.FailureStatusCode ?? StatusCodes.Status503ServiceUnavailable;
            if (!string.IsNullOrWhiteSpace(resolved.FailureMessage))
                await context.Response.WriteAsync(resolved.FailureMessage, cancellationToken);
            return;
        }

        if (!await streamingSettings.GetEnabledAsync(cancellationToken))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Append("Retry-After", "60");
            await context.Response.WriteAsync("Stream proxy is disabled.", cancellationToken);
            return;
        }

        var entry = resolved.Entry;
        logger.LogInformation("Stream tune-in: channel={Channel} key={StreamKey} client={Client}",
            entry.DisplayName, streamKey, context.Connection.RemoteIpAddress);

        if (resolved.UseSharedSession && resolved.SourceDescriptor is not null)
        {
            try
            {
                var session = await channelSessionManager.GetOrCreateAsync(resolved.SourceDescriptor, cancellationToken);
                var subscriber = await session.AttachSubscriberAsync(context, cancellationToken);
                await subscriber.Completion;
                return;
            }
            catch (StreamAdmissionException ex)
            {
                logger.LogWarning(
                    "Shared stream admission rejected for {ProviderId}/{ProviderChannelId}: {Reason}",
                    resolved.SourceDescriptor.ProviderId,
                    resolved.SourceDescriptor.ProviderChannelId,
                    ex.Message);
                if (ex.RetryAfterSeconds is { } retryAfter)
                    context.Response.Headers["Retry-After"] = retryAfter.ToString();

                context.Response.StatusCode = ex.StatusCode;
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (UpstreamConnectException ex)
            {
                logger.LogWarning(
                    "Shared stream upstream startup/connect failure for key={StreamKey}. kind={FailureKind} status={StatusCode}",
                    streamKey,
                    ex.FailureKind,
                    ex.StatusCode);

                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = ex.FailureKind is UpstreamFailureKind.UpstreamAuth
                        or UpstreamFailureKind.UpstreamNotFound
                        or UpstreamFailureKind.StartupFatal
                        ? StatusCodes.Status502BadGateway
                        : StatusCodes.Status503ServiceUnavailable;
                }
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Shared stream delivery failed for key={StreamKey}.", streamKey);
                if (!context.Response.HasStarted)
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
        }

        await ServeDirectRelayAsync(
            context,
            db,
            httpClientFactory,
            logger,
            entry.StreamUrl,
            entry.DisplayName,
            cancellationToken);
    }

    private static async Task ServeDirectRelayAsync(
        HttpContext context,
        ApplicationDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        string streamUrl,
        string displayName,
        CancellationToken cancellationToken)
    {
        var provider = await ResolveProviderForDirectRelayAsync(db, context, cancellationToken);

        try
        {
            using var client = httpClientFactory.CreateClient("stream-relay");

            if (provider is not null)
            {
                ProviderFetcher.ApplyHeadersFromJson(client, provider.HeadersJson);
                if (!string.IsNullOrWhiteSpace(provider.UserAgent))
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(provider.UserAgent);
            }

            if (context.Request.Headers.TryGetValue("Range", out var rangeValue))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Range", rangeValue.ToArray());

            using var upstreamResponse = await client.GetAsync(
                streamUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            logger.LogInformation("Stream upstream: channel={Channel} status={Status} contentType={ContentType}",
                displayName,
                (int)upstreamResponse.StatusCode,
                upstreamResponse.Content.Headers.ContentType?.ToString() ?? "none");

            context.Response.StatusCode = (int)upstreamResponse.StatusCode;

            if (upstreamResponse.Content.Headers.ContentType is not null)
                context.Response.ContentType = upstreamResponse.Content.Headers.ContentType.ToString();

            if (upstreamResponse.Content.Headers.ContentLength.HasValue)
                context.Response.ContentLength = upstreamResponse.Content.Headers.ContentLength.Value;

            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
            await upstreamStream.CopyToAsync(context.Response.Body, cancellationToken);

            logger.LogDebug("Stream complete: channel={Channel} client={Client}",
                displayName,
                context.Connection.RemoteIpAddress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Stream client disconnected: channel={Channel} client={Client}",
                displayName,
                context.Connection.RemoteIpAddress);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Stream upstream request failed: channel={Channel} key={StreamKey}",
                displayName,
                "direct-relay");
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    private static async Task<Provider?> ResolveProviderForDirectRelayAsync(
        ApplicationDbContext db,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var access = context.GetResolvedClientAccess();
        var profileProvider = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.ProfileId == access.Binding.ActiveProfileId && x.Enabled)
            .OrderBy(x => x.Priority)
            .FirstOrDefaultAsync(cancellationToken);

        if (profileProvider is null)
        {
            return await db.Providers
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, cancellationToken);
        }

        return await db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProviderId == profileProvider.ProviderId && x.Enabled, cancellationToken);
    }

    private static async Task ServeStatusAsync(HttpContext context, ApplicationDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            var activeSnapshot = await db.Snapshots
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Status == "active", cancellationToken);

            var activeProvider = await db.Providers
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, cancellationToken);

            FetchRunInfo? lastRefresh = null;
            if (activeProvider is not null)
            {
                var run = await db.FetchRuns
                    .AsNoTracking()
                    .Where(x => x.ProviderId == activeProvider.ProviderId && x.Type == "snapshot")
                    .OrderByDescending(x => x.StartedUtc)
                    .FirstOrDefaultAsync(cancellationToken);

                if (run is not null)
                {
                    lastRefresh = new FetchRunInfo(run.Status, run.StartedUtc, run.FinishedUtc, run.ChannelCountSeen, run.ErrorSummary);
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

            var status = new StatusResponse(Status: lineupStatus, Lineups: [lineup]);

            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(context.Response.Body, status, JsonOptions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected before response completed
        }
    }

    private static IResult ServeStreamsStatusSummaryAsync(StreamingRegistry registry)
    {
        var sessions = registry.GetActiveSessions();
        var summary = new StreamStatusSummary(
            ActiveSessionCount: sessions.Count,
            ActiveSubscriberCount: sessions.Sum(x => x.SubscriberCount),
            SessionsReconnecting: sessions.Count(x => x.State == SessionState.Reconnecting),
            TotalReconnectAttempts: sessions.Sum(x => x.ReconnectAttempts),
            ActiveSessions: sessions,
            RecentEndedSessions: registry.GetRecentEndedSessions());
        return Results.Json(summary, JsonOptions);
    }

    private static IResult ServeStreamsClientsStatusAsync(StreamingRegistry registry)
        => Results.Json(registry.GetActiveClients(), JsonOptions);

    private static IResult ServeStreamsProvidersStatusAsync(StreamingRegistry registry)
        => Results.Json(registry.GetActiveProviderStreams(), JsonOptions);

    private static IResult ServeStreamsSingleSessionStatusAsync(string sessionId, StreamingRegistry registry)
    {
        var snapshot = registry.TryGetSession(sessionId);
        return snapshot is null
            ? TypedResults.NotFound()
            : Results.Json(snapshot, JsonOptions);
    }

    private sealed record StatusResponse(
        string Status,
        IReadOnlyList<LineupStatusInfo> Lineups);

    private sealed record LineupStatusInfo(
        string Name,
        string Status,
        ActiveProviderInfo? ActiveProvider,
        ActiveSnapshotInfo? ActiveSnapshot,
        FetchRunInfo? LastRefresh);

    private sealed record ActiveProviderInfo(string ProviderId, string Name);

    private sealed record ActiveSnapshotInfo(
        string SnapshotId,
        string ProfileId,
        DateTime CreatedUtc,
        int ChannelCountPublished);

    private sealed record FetchRunInfo(
        string Status,
        DateTime StartedUtc,
        DateTime? FinishedUtc,
        int? ChannelCountSeen,
        string? ErrorSummary);

    private sealed record StreamStatusSummary(
        int ActiveSessionCount,
        int ActiveSubscriberCount,
        int SessionsReconnecting,
        int TotalReconnectAttempts,
        IReadOnlyList<StreamSessionSnapshot> ActiveSessions,
        IReadOnlyList<StreamSessionSnapshot> RecentEndedSessions);
}
