using System.Text;
using System.Text.Json;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Security;
using M3Undle.Web.Streaming.Compatibility;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Streaming.Observability;
using M3Undle.Web.Streaming.Resolution;
using M3Undle.Web.Streaming.Sessions;
using M3Undle.Web.Streaming.Subscribers;
using M3Undle.Web.Streaming.Upstream;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
        client.MapGet("hls/{streamKey}/proxy", ServeHlsProxyAsync);

        app.MapGet("/status", ServeStatusAsync).AllowAnonymous();
        app.MapGet("/health/ready", ServeReadinessAsync).AllowAnonymous();

        var streamStatus = app.MapGroup("/status/streams")
            .RequireAuthorization(UiAccessPolicy.Name);
        streamStatus.MapGet(string.Empty, ServeStreamsStatusSummaryAsync);
        streamStatus.MapGet("clients", ServeStreamsClientsStatusAsync);
        streamStatus.MapGet("providers", ServeStreamsProvidersStatusAsync);
        streamStatus.MapGet("{sessionId}", ServeStreamsSingleSessionStatusAsync);

        // Test-mode debug endpoints — only registered when M3UNDLE_TEST_MODE=true
        var testMode = string.Equals(
            Environment.GetEnvironmentVariable("M3UNDLE_TEST_MODE")?.Trim(),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (testMode)
        {
            var debug = app.MapGroup("/debug").RequireAuthorization(UiAccessPolicy.Name);
            debug.MapPost("/streams/reset", ServeDebugStreamResetAsync);
            debug.MapPost("/strikes/reset", ServeDebugStrikeResetAsync);
            debug.MapGet("/streams/strikes", ServeDebugStrikesAsync);
        }

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
        HdHomeRunTunerManager hdHomeRunTunerManager,
        IHttpClientFactory httpClientFactory,
        HlsProxyService hlsProxyService,
        ILoggerFactory loggerFactory,
        IOptions<StreamProxyOptions> streamProxyOptions,
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

        if (!streamProxyOptions.Value.StreamingEnabled)
        {
            logger.LogWarning("Stream request rejected for key={StreamKey} — stream proxy is disabled in configuration.", streamKey);
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
            // Only attempt HLS delivery when the client can consume it.
            // HDHR/tune routes are always raw TS (used by Plex, Channels DVR).
            // A .ts tail from a non-browser client means a native app expecting raw bytes.
            // Browser-based apps (IPTVnator, Electron) send Mozilla UA and need HLS.
            var tail = context.Request.RouteValues.TryGetValue("tail", out var tailVal)
                ? tailVal?.ToString() ?? string.Empty
                : string.Empty;
            var isNativeClientRoute = IsHdHomeRunTuneRoute(context.Request.Path)
                || (!IsBrowserClient(context) && tail.EndsWith(".ts", StringComparison.OrdinalIgnoreCase));

            if (!isNativeClientRoute)
            {
                var hlsCandidates = HlsDetection.GetHlsCandidates(resolved.SourceDescriptor.StreamUrl);
                if (hlsCandidates.Count > 0)
                {
                    var baseUrl = GetBaseUrl(context);
                    var segmentProxyBase = $"{baseUrl}/hls/{Uri.EscapeDataString(streamKey)}/proxy";
                    segmentProxyBase = segmentProxyBase.ApplyClientAccessQuery(context);

                    var manifest = await hlsProxyService.FetchAndRewriteManifestAsync(
                        hlsCandidates, resolved.SourceDescriptor, segmentProxyBase, cancellationToken);

                    if (manifest is not null)
                    {
                        logger.LogInformation(
                            "HLS delivery: channel={Channel} key={StreamKey}",
                            entry.DisplayName, streamKey);
                        context.Response.ContentType = "application/vnd.apple.mpegurl";
                        context.Response.Headers.CacheControl = "no-cache";
                        await context.Response.WriteAsync(manifest, cancellationToken);
                        return;
                    }

                    logger.LogInformation(
                        "HLS not available for '{Channel}', falling back to TS session.",
                        entry.DisplayName);
                }
            }

            HdHomeRunTunerReservation? tunerReservation = null;
            SubscriberConnection? subscriber = null;
            try
            {
                if (IsHdHomeRunTuneRoute(context.Request.Path))
                {
                    var access = context.GetResolvedClientAccess();
                    var tunerId = string.IsNullOrWhiteSpace(access.Binding.VirtualTunerId)
                        ? "hdhr-main"
                        : access.Binding.VirtualTunerId!;

                    var tunerAcquire = hdHomeRunTunerManager.Acquire(tunerId, streamKey);
                    if (!tunerAcquire.Succeeded || tunerAcquire.Reservation is null)
                    {
                        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        context.Response.Headers["Retry-After"] = "30";
                        if (!string.IsNullOrWhiteSpace(tunerAcquire.Error))
                            await context.Response.WriteAsync(tunerAcquire.Error, cancellationToken);
                        return;
                    }

                    tunerReservation = tunerAcquire.Reservation;
                    if (tunerAcquire.PriorSubscriber is not null)
                        await tunerAcquire.PriorSubscriber.CompleteAsync(SubscriberDisconnectReason.Retuned);
                }

                var session = await channelSessionManager.GetOrCreateAsync(resolved.SourceDescriptor, cancellationToken);
                subscriber = await session.AttachSubscriberAsync(context, cancellationToken);

                if (tunerReservation is not null)
                {
                    hdHomeRunTunerManager.Activate(
                        tunerReservation,
                        subscriber,
                        resolved.SourceDescriptor.DisplayName);
                }

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
            finally
            {
                if (tunerReservation is not null)
                    hdHomeRunTunerManager.Release(tunerReservation.ReservationId, subscriber?.ClientId);
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

    private static async Task ServeHlsProxyAsync(
        string streamKey,
        HttpContext context,
        HlsProxyService hlsProxyService,
        StreamRequestResolver streamRequestResolver,
        string? u,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(u))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Missing 'u' parameter.", cancellationToken);
            return;
        }

        string upstreamUrl;
        try
        {
            upstreamUrl = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(u));
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid 'u' parameter.", cancellationToken);
            return;
        }

        if (!Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid upstream URL.", cancellationToken);
            return;
        }

        var baseUrl = GetBaseUrl(context);
        var segmentProxyBase = $"{baseUrl}/hls/{Uri.EscapeDataString(streamKey)}/proxy";
        segmentProxyBase = segmentProxyBase.ApplyClientAccessQuery(context);

        string providerId;
        try
        {
            var resolved = await streamRequestResolver.ResolveAsync(streamKey, context, cancellationToken);
            if (!resolved.IsSuccess || resolved.SourceDescriptor is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("Unknown stream key.", cancellationToken);
                return;
            }

            providerId = resolved.SourceDescriptor.ProviderId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Unknown stream key.", cancellationToken);
            return;
        }

        await hlsProxyService.ProxyAsync(context, upstreamUrl, segmentProxyBase, providerId, cancellationToken);
    }

    private static string GetBaseUrl(HttpContext context)
    {
        var pathBase = context.Request.PathBase.HasValue
            ? context.Request.PathBase.Value?.TrimEnd('/')
            : null;

        return string.IsNullOrWhiteSpace(pathBase)
            ? $"{context.Request.Scheme}://{context.Request.Host}"
            : $"{context.Request.Scheme}://{context.Request.Host}{pathBase}";
    }

    /// <summary>
    /// Returns true when the request originates from a browser or Electron-based app.
    /// These clients cannot decode raw MPEG-TS and require HLS delivery.
    /// Native IPTV apps (TiviMate, IPTVator, Smarters) use non-browser User-Agent strings.
    /// </summary>
    private static bool IsBrowserClient(HttpContext context) =>
        context.Request.Headers.UserAgent.ToString()
            .Contains("Mozilla/", StringComparison.OrdinalIgnoreCase);

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

            logger.LogInformation("Stream ended: channel={Channel} client={Client}",
                displayName,
                context.Connection.RemoteIpAddress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Client disconnected from stream: channel={Channel} client={Client}",
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

    private static async Task<IResult> ServeReadinessAsync(
        ApplicationDbContext db,
        IRefreshTrigger refreshTrigger,
        CancellationToken cancellationToken)
    {
        var reasons = new List<string>();

        if (!await db.Providers.AsNoTracking().AnyAsync(x => x.IsActive && x.Enabled, cancellationToken))
            reasons.Add("no active provider");

        if (!await db.Snapshots.AsNoTracking().AnyAsync(x => x.Status == "active", cancellationToken))
            reasons.Add("no active snapshot");

        if (refreshTrigger.IsRefreshing)
            reasons.Add("refresh in progress");

        return reasons.Count == 0
            ? Results.Ok(new { ready = true })
            : Results.Json(new { ready = false, reasons }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult ServeDebugStrikesAsync(UpstreamFailureStrikeStore strikeStore)
    {
        var cooldowns = strikeStore.GetActiveCooldowns();
        var dtos = cooldowns
            .Select(c => new
            {
                sessionKey = c.Key.ToString(),
                providerId = c.Key.ProviderId,
                providerChannelId = c.Key.ProviderChannelId,
                remainingSeconds = Math.Round(c.Remaining.TotalSeconds, 1),
            })
            .ToArray();
        return Results.Json(dtos, JsonOptions);
    }

    private static async Task<IResult> ServeDebugStreamResetAsync(ChannelSessionManager sessionManager)
    {
        await sessionManager.ResetAllAsync();
        return Results.Ok(new { cleared = true });
    }

    private static IResult ServeDebugStrikeResetAsync(UpstreamFailureStrikeStore strikeStore)
    {
        strikeStore.ClearAll();
        return Results.Ok(new { cleared = true });
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

    // `/tune/*` exists only as the legacy HDHomeRun root alias for `/hdhr/tune/*`.
    private static bool IsHdHomeRunTuneRoute(PathString path)
        => path.StartsWithSegments("/hdhr/tune", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/tune", StringComparison.OrdinalIgnoreCase);

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
