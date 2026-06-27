using System.Text;
using System.Text.Json;
using System.Globalization;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Security;
using M3Undle.Web.Streaming.Compatibility;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.GeneratedHls;
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
        client.MapGet("tuner{tuner:int}/v{vchannel}", ServeHdhrNativeTuneByVChannelAsync);
        client.MapGet("tuner{tuner:int}/ch{channel}", ServeHdhrNativeTuneByChannelAsync);
        client.MapGet("tuner{tuner:int}/auto/v{vchannel}", ServeHdhrNativeTuneByVChannelAsync);
        client.MapGet("tuner{tuner:int}/auto/ch{channel}", ServeHdhrNativeTuneByChannelAsync);
        client.MapGet("tuner{tuner:int}/auto/{channel}", ServeHdhrNativeTuneByChannelAsync);
        client.MapGet("hdhr/tuner{tuner:int}/v{vchannel}", ServeHdhrNativeTuneByVChannelAsync);
        client.MapGet("hdhr/tuner{tuner:int}/ch{channel}", ServeHdhrNativeTuneByChannelAsync);
        client.MapGet("hdhr/tuner{tuner:int}/auto/v{vchannel}", ServeHdhrNativeTuneByVChannelAsync);
        client.MapGet("hdhr/tuner{tuner:int}/auto/ch{channel}", ServeHdhrNativeTuneByChannelAsync);
        client.MapGet("hdhr/tuner{tuner:int}/auto/{channel}", ServeHdhrNativeTuneByChannelAsync);
        client.MapGet("hdhr/auto/v{vchannel}", ServeHdhrAutoTuneByVChannelAsync);
        client.MapGet("hdhr/auto/ch{channel}", ServeHdhrAutoTuneByChannelAsync);
        client.MapGet("hdhr/auto/{channel}", ServeHdhrAutoTuneByChannelAsync);
        client.MapGet("auto/v{vchannel}", ServeHdhrAutoTuneByVChannelAsync);
        client.MapGet("auto/ch{channel}", ServeHdhrAutoTuneByChannelAsync);
        client.MapGet("auto/{channel}", ServeHdhrAutoTuneByChannelAsync);
        client.MapGet("hls/{streamKey}/index.m3u8", ServeExternalHlsManifestAsync);
        client.MapGet("hls/{streamKey}/proxy", ServeHlsProxyAsync);
        client.MapGet("hls/generated/{sessionId}/{*asset}", ServeGeneratedHlsAssetAsync);

        // Internal relay: lets Generated HLS sessions read from the shared ring buffer instead
        // of opening a second provider connection. Protected by startup-generated secret header.
        app.MapGet("/internal/relay/{providerId}/{channelId}", ServeInternalRelayAsync).AllowAnonymous();
        app.MapGet("/internal/relay/{providerId}/{channelId}.ts", ServeInternalRelayAsync).AllowAnonymous();

        app.MapGet("/status", ServeStatusAsync).AllowAnonymous();
        app.MapGet("/health/ready", ServeReadinessAsync).AllowAnonymous();

        var streamStatus = app.MapGroup("/status/streams")
            .RequireAuthorization(UiAccessPolicy.Name);
        streamStatus.MapGet(string.Empty, ServeStreamsStatusSummaryAsync);
        streamStatus.MapGet("clients", ServeStreamsClientsStatusAsync);
        streamStatus.MapGet("providers", ServeStreamsProvidersStatusAsync);
        streamStatus.MapGet("events", ServeStreamsEventsStatusAsync);
        streamStatus.MapGet("{sessionId}/events", ServeStreamsSingleSessionEventsStatusAsync);
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
            debug.MapGet("/streams/rca", ServeDebugStreamRcaAsync);
            debug.MapGet("/snapshots/idle", ServeDebugSnapshotIdleAsync);
            debug.MapPost("/stream-health/events/seed", ServeDebugStreamHealthSeedAsync);
            debug.MapDelete("/stream-health/events/{providerId}/{providerChannelId}", ServeDebugStreamHealthDeleteAsync);
            debug.MapGet("/stream-health/{providerId}/{providerChannelId}", ServeDebugStreamHealthAsync);
        }

        return app;
    }

    private static async Task ServeM3uAsync(
        HttpContext context,
        ILineupRenderer lineupRenderer,
        IM3USerializer m3uSerializer,
        ApplicationDbContext db,
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

            var minExpiry = await db.ProfileProviders
                .Where(pp => pp.ProfileId == access.Binding.ActiveProfileId && pp.Enabled)
                .Join(db.Providers.Where(p => p.Enabled), pp => pp.ProviderId, p => p.ProviderId, (pp, p) => p.PlaylistExpiresUtc)
                .Where(e => e != null)
                .MinAsync(e => e, cancellationToken);

            await m3uSerializer.WriteAsync(context, lineup, minExpiry, cancellationToken);
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

    private static Task ServeHdhrNativeTuneByVChannelAsync(
        int tuner,
        string vchannel,
        HttpContext context,
        ApplicationDbContext db,
        StreamRequestResolver streamRequestResolver,
        ChannelSessionManager channelSessionManager,
        HdHomeRunTunerManager hdHomeRunTunerManager,
        HdHomeRunDeviceService hdHomeRunDeviceService,
        IHttpClientFactory httpClientFactory,
        HlsProxyService hlsProxyService,
        GeneratedHlsSessionManager generatedHlsSessionManager,
        ILineupRenderer lineupRenderer,
        HdHomeRunLineupService hdHomeRunLineupService,
        ILoggerFactory loggerFactory,
        IOptions<StreamProxyOptions> streamProxyOptions,
        CancellationToken cancellationToken)
        => ServeHdhrNativeTuneAsync(
            tuner,
            requestedGuideNumber: vchannel,
            context,
            db,
            streamRequestResolver,
            channelSessionManager,
            hdHomeRunTunerManager,
            hdHomeRunDeviceService,
            httpClientFactory,
            hlsProxyService,
            generatedHlsSessionManager,
            lineupRenderer,
            hdHomeRunLineupService,
            loggerFactory,
            streamProxyOptions,
            cancellationToken);

    private static Task ServeHdhrNativeTuneByChannelAsync(
        int tuner,
        string channel,
        HttpContext context,
        ApplicationDbContext db,
        StreamRequestResolver streamRequestResolver,
        ChannelSessionManager channelSessionManager,
        HdHomeRunTunerManager hdHomeRunTunerManager,
        HdHomeRunDeviceService hdHomeRunDeviceService,
        IHttpClientFactory httpClientFactory,
        HlsProxyService hlsProxyService,
        GeneratedHlsSessionManager generatedHlsSessionManager,
        ILineupRenderer lineupRenderer,
        HdHomeRunLineupService hdHomeRunLineupService,
        ILoggerFactory loggerFactory,
        IOptions<StreamProxyOptions> streamProxyOptions,
        CancellationToken cancellationToken)
        => ServeHdhrNativeTuneAsync(
            tuner,
            requestedGuideNumber: NormalizeHdhrGuideNumber(channel),
            context,
            db,
            streamRequestResolver,
            channelSessionManager,
            hdHomeRunTunerManager,
            hdHomeRunDeviceService,
            httpClientFactory,
            hlsProxyService,
            generatedHlsSessionManager,
            lineupRenderer,
            hdHomeRunLineupService,
            loggerFactory,
            streamProxyOptions,
            cancellationToken);

    private static Task ServeHdhrAutoTuneByVChannelAsync(
        string vchannel,
        HttpContext context,
        ApplicationDbContext db,
        StreamRequestResolver streamRequestResolver,
        ChannelSessionManager channelSessionManager,
        HdHomeRunTunerManager hdHomeRunTunerManager,
        HdHomeRunDeviceService hdHomeRunDeviceService,
        IHttpClientFactory httpClientFactory,
        HlsProxyService hlsProxyService,
        GeneratedHlsSessionManager generatedHlsSessionManager,
        ILineupRenderer lineupRenderer,
        HdHomeRunLineupService hdHomeRunLineupService,
        ILoggerFactory loggerFactory,
        IOptions<StreamProxyOptions> streamProxyOptions,
        CancellationToken cancellationToken)
        => ServeHdhrAutoTuneAsync(
            requestedGuideNumber: vchannel,
            context,
            db,
            streamRequestResolver,
            channelSessionManager,
            hdHomeRunTunerManager,
            hdHomeRunDeviceService,
            httpClientFactory,
            hlsProxyService,
            generatedHlsSessionManager,
            lineupRenderer,
            hdHomeRunLineupService,
            loggerFactory,
            streamProxyOptions,
            cancellationToken);

    private static Task ServeHdhrAutoTuneByChannelAsync(
        string channel,
        HttpContext context,
        ApplicationDbContext db,
        StreamRequestResolver streamRequestResolver,
        ChannelSessionManager channelSessionManager,
        HdHomeRunTunerManager hdHomeRunTunerManager,
        HdHomeRunDeviceService hdHomeRunDeviceService,
        IHttpClientFactory httpClientFactory,
        HlsProxyService hlsProxyService,
        GeneratedHlsSessionManager generatedHlsSessionManager,
        ILineupRenderer lineupRenderer,
        HdHomeRunLineupService hdHomeRunLineupService,
        ILoggerFactory loggerFactory,
        IOptions<StreamProxyOptions> streamProxyOptions,
        CancellationToken cancellationToken)
        => ServeHdhrAutoTuneAsync(
            requestedGuideNumber: NormalizeHdhrGuideNumber(channel),
            context,
            db,
            streamRequestResolver,
            channelSessionManager,
            hdHomeRunTunerManager,
            hdHomeRunDeviceService,
            httpClientFactory,
            hlsProxyService,
            generatedHlsSessionManager,
            lineupRenderer,
            hdHomeRunLineupService,
            loggerFactory,
            streamProxyOptions,
            cancellationToken);

    private static async Task ServeHdhrAutoTuneAsync(
        string requestedGuideNumber,
        HttpContext context,
        ApplicationDbContext db,
        StreamRequestResolver streamRequestResolver,
        ChannelSessionManager channelSessionManager,
        HdHomeRunTunerManager hdHomeRunTunerManager,
        HdHomeRunDeviceService hdHomeRunDeviceService,
        IHttpClientFactory httpClientFactory,
        HlsProxyService hlsProxyService,
        GeneratedHlsSessionManager generatedHlsSessionManager,
        ILineupRenderer lineupRenderer,
        HdHomeRunLineupService hdHomeRunLineupService,
        ILoggerFactory loggerFactory,
        IOptions<StreamProxyOptions> streamProxyOptions,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("M3Undle.Stream");

        if (!hdHomeRunDeviceService.IsEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        requestedGuideNumber = requestedGuideNumber.Trim();
        if (string.IsNullOrWhiteSpace(requestedGuideNumber))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        string streamKey;
        try
        {
            var access = context.GetResolvedClientAccess();
            var renderedLineup = await lineupRenderer.TryRenderActiveLineupAsync(access.Binding.ActiveProfileId, cancellationToken);
            if (renderedLineup is null)
            {
                logger.LogWarning(
                    "HDHR auto tune failed: no active snapshot. path={Path} guide={GuideNumber} profile={ProfileId}",
                    context.Request.Path.Value,
                    requestedGuideNumber,
                    access.Binding.ActiveProfileId);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.Append("Retry-After", "60");
                await context.Response.WriteAsync("No active snapshot available. Waiting for first refresh.", cancellationToken);
                return;
            }

            var resolvedStreamKey = hdHomeRunLineupService.TryResolveStreamKeyByGuideNumber(renderedLineup, requestedGuideNumber, cancellationToken);
            if (string.IsNullOrWhiteSpace(resolvedStreamKey))
            {
                logger.LogWarning(
                    "HDHR auto tune failed: guide number not found in active lineup. path={Path} guide={GuideNumber} profile={ProfileId}",
                    context.Request.Path.Value,
                    requestedGuideNumber,
                    access.Binding.ActiveProfileId);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            streamKey = resolvedStreamKey;
            logger.LogInformation(
                "HDHR auto tune resolved. path={Path} guide={GuideNumber} streamKey={StreamKey}",
                context.Request.Path.Value,
                requestedGuideNumber,
                streamKey);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.LogWarning(
                ex,
                "HDHR auto tune failed: active snapshot data unavailable. path={Path} guide={GuideNumber}",
                context.Request.Path.Value,
                requestedGuideNumber);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Append("Retry-After", "30");
            await context.Response.WriteAsync("Active snapshot data is unavailable.", cancellationToken);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await ServeStreamAsync(
            streamKey,
            context,
            db,
            streamRequestResolver,
            channelSessionManager,
            hdHomeRunTunerManager,
            httpClientFactory,
            hlsProxyService,
            generatedHlsSessionManager,
            loggerFactory,
            streamProxyOptions,
            cancellationToken);
    }

    private static async Task ServeHdhrNativeTuneAsync(
        int tuner,
        string requestedGuideNumber,
        HttpContext context,
        ApplicationDbContext db,
        StreamRequestResolver streamRequestResolver,
        ChannelSessionManager channelSessionManager,
        HdHomeRunTunerManager hdHomeRunTunerManager,
        HdHomeRunDeviceService hdHomeRunDeviceService,
        IHttpClientFactory httpClientFactory,
        HlsProxyService hlsProxyService,
        GeneratedHlsSessionManager generatedHlsSessionManager,
        ILineupRenderer lineupRenderer,
        HdHomeRunLineupService hdHomeRunLineupService,
        ILoggerFactory loggerFactory,
        IOptions<StreamProxyOptions> streamProxyOptions,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("M3Undle.Stream");
        if (!hdHomeRunDeviceService.IsEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (tuner < 0 || !hdHomeRunTunerManager.IsValidTunerIndex(tuner))
        {
            logger.LogWarning(
                "HDHR native tuner request rejected: invalid tuner index. path={Path} tuner={Tuner}",
                context.Request.Path.Value,
                tuner);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        requestedGuideNumber = requestedGuideNumber.Trim();
        if (string.IsNullOrWhiteSpace(requestedGuideNumber))
        {
            logger.LogWarning(
                "HDHR native tuner request rejected: empty guide number. path={Path} tuner={Tuner}",
                context.Request.Path.Value,
                tuner);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        string streamKey;
        try
        {
            var access = context.GetResolvedClientAccess();
            var renderedLineup = await lineupRenderer.TryRenderActiveLineupAsync(access.Binding.ActiveProfileId, cancellationToken);
            if (renderedLineup is null)
            {
                logger.LogWarning(
                    "HDHR native tuner request failed: no active snapshot. path={Path} tuner={Tuner} guide={GuideNumber} profile={ProfileId}",
                    context.Request.Path.Value,
                    tuner,
                    requestedGuideNumber,
                    access.Binding.ActiveProfileId);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.Append("Retry-After", "60");
                await context.Response.WriteAsync("No active snapshot available. Waiting for first refresh.", cancellationToken);
                return;
            }

            var resolvedStreamKey = hdHomeRunLineupService.TryResolveStreamKeyByGuideNumber(renderedLineup, requestedGuideNumber, cancellationToken);
            if (string.IsNullOrWhiteSpace(resolvedStreamKey))
            {
                logger.LogWarning(
                    "HDHR native tuner request failed: guide number not found in active lineup. path={Path} tuner={Tuner} guide={GuideNumber} profile={ProfileId}",
                    context.Request.Path.Value,
                    tuner,
                    requestedGuideNumber,
                    access.Binding.ActiveProfileId);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            streamKey = resolvedStreamKey;
            logger.LogInformation(
                "HDHR native tuner request resolved. path={Path} tuner={Tuner} guide={GuideNumber} streamKey={StreamKey}",
                context.Request.Path.Value,
                tuner,
                requestedGuideNumber,
                streamKey);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.LogWarning(
                ex,
                "HDHR native tuner request failed: active snapshot data unavailable. path={Path} tuner={Tuner} guide={GuideNumber}",
                context.Request.Path.Value,
                tuner,
                requestedGuideNumber);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Append("Retry-After", "30");
            await context.Response.WriteAsync("Active snapshot data is unavailable.", cancellationToken);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await ServeStreamAsync(
            streamKey,
            context,
            db,
            streamRequestResolver,
            channelSessionManager,
            hdHomeRunTunerManager,
            httpClientFactory,
            hlsProxyService,
            generatedHlsSessionManager,
            loggerFactory,
            streamProxyOptions,
            cancellationToken);
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
        GeneratedHlsSessionManager generatedHlsSessionManager,
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

        var forceTs = IsHdHomeRunTuneRoute(context.Request.Path) || (resolved.SourceDescriptor?.ForceMpegTs ?? false);

        if (!forceTs && resolved.UseSharedSession && PlaybackModeResolver.IsBurstBufferingClient(context))
        {
            var hlsUrl = $"{GetBaseUrl(context)}/hls/{Uri.EscapeDataString(streamKey)}/index.m3u8";
            hlsUrl = hlsUrl.ApplyClientAccessQuery(context);
            logger.LogInformation(
                "Auto-HLS redirect: channel={Channel} key={StreamKey} client={Client}",
                entry.DisplayName, streamKey, context.Connection.RemoteIpAddress);
            context.Response.Redirect(hlsUrl, permanent: false);
            return;
        }

        var requiresHls = PlaybackModeResolver.RequiresHls(context, forceTs);

        // HLS slot reservation only applies to non-shared (native upstream HLS) sessions.
        // Shared sessions use generated HLS wrapper + ring buffer relay — no slot needed.
        ChannelSessionManager.HlsSlotReservation? hlsSlotReservation = null;
        if (requiresHls && !resolved.UseSharedSession && resolved.SourceDescriptor is not null)
        {
            try
            {
                hlsSlotReservation = channelSessionManager.ReserveHlsSlot(resolved.SourceDescriptor);
            }
            catch (StreamAdmissionException ex)
            {
                logger.LogWarning(
                    "HLS live stream admission rejected for {ProviderId}/{ProviderChannelId}: {Reason}",
                    resolved.SourceDescriptor.ProviderId,
                    resolved.SourceDescriptor.ProviderChannelId,
                    ex.Message);
                if (ex.RetryAfterSeconds is { } retry)
                    context.Response.Headers["Retry-After"] = retry.ToString();
                context.Response.StatusCode = ex.StatusCode;
                return;
            }
        }

        if (requiresHls)
        {
            if (resolved.SourceDescriptor is null)
            {
                hlsSlotReservation?.Dispose();
                logger.LogWarning(
                    "HLS request could not be fulfilled for key={StreamKey}: provider stream metadata unavailable.",
                    streamKey);
                await StreamErrorResponse.WriteHtmlErrorAsync(
                    context.Response, StatusCodes.Status503ServiceUnavailable,
                    "HLS delivery is unavailable for this stream.", cancellationToken);
                return;
            }

            // Native upstream HLS is only used for non-shared sessions.
            // For shared sessions, generated HLS wraps the ring buffer relay so all clients share one upstream.
            if (!resolved.UseSharedSession)
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
                        hlsSlotReservation?.Dispose();
                        logger.LogInformation(
                            "Native upstream HLS delivery: channel={Channel} key={StreamKey}",
                            entry.DisplayName,
                            streamKey);
                        context.Response.ContentType = "application/vnd.apple.mpegurl";
                        context.Response.Headers.CacheControl = "no-cache";
                        await context.Response.WriteAsync(manifest, cancellationToken);
                        return;
                    }
                }
            }

            var generatedStreamUrl = resolved.SourceDescriptor.StreamUrl;
            string? generatedRelaySecret = null;
            string? parentStreamSessionId = null;
            var parentSession = await channelSessionManager.TryGetOrCreateForGeneratedHlsAsync(
                resolved.SourceDescriptor,
                resolved.UseSharedSession,
                cancellationToken);
            if (parentSession is not null)
            {
                var sk = resolved.SourceDescriptor.SessionKey;
                parentStreamSessionId = parentSession.SessionId;
                generatedStreamUrl =
                    $"http://127.0.0.1:{context.Connection.LocalPort}/internal/relay/{Uri.EscapeDataString(sk.ProviderId)}/{Uri.EscapeDataString(sk.ProviderChannelId)}.ts";
                generatedRelaySecret = context.RequestServices.GetRequiredService<InternalRelaySecretService>().Secret;
            }

            var generatedSession = await generatedHlsSessionManager.CreateSessionAsync(
                new GeneratedHlsSessionRequest(
                    StreamUrl: generatedStreamUrl,
                    DisplayName: resolved.SourceDescriptor.DisplayName,
                    ProviderId: generatedRelaySecret is null ? resolved.SourceDescriptor.ProviderId : null,
                    AdmissionKey: resolved.SourceDescriptor.SessionKey,
                    InternalRelaySecret: generatedRelaySecret,
                    ParentStreamSessionId: parentStreamSessionId,
                    RequestedRoute: resolved.SourceDescriptor.RequestedRoute),
                cancellationToken);

            if (generatedSession is null)
            {
                hlsSlotReservation?.Dispose();
                logger.LogWarning(
                    "Generated HLS startup failed for key={StreamKey}.",
                    streamKey);
                await StreamErrorResponse.WriteHtmlErrorAsync(
                    context.Response, StatusCodes.Status503ServiceUnavailable,
                    "Generated HLS is unavailable for this stream.", cancellationToken);
                return;
            }

            var pathBase = context.Request.PathBase.HasValue
                ? context.Request.PathBase.Value!.TrimEnd('/')
                : string.Empty;
            var generatedManifestUrl =
                $"{pathBase}/hls/generated/{Uri.EscapeDataString(generatedSession.SessionId)}/index.m3u8";
            generatedManifestUrl = generatedManifestUrl.ApplyClientAccessQuery(context);

            logger.LogInformation(
                "Generated HLS delivery: channel={Channel} key={StreamKey} session={SessionId}",
                entry.DisplayName,
                streamKey,
                generatedSession.SessionId);

            context.Response.Redirect(generatedManifestUrl, permanent: false);
            return;
        }

        if (resolved.UseSharedSession && resolved.SourceDescriptor is not null)
        {
            HdHomeRunTunerReservation? tunerReservation = null;
            SubscriberConnection? subscriber = null;
            IDisposable? tunerSessionRetention = null;
            try
            {
                if (IsHdHomeRunTuneRoute(context.Request.Path))
                {
                    HdHomeRunTunerAcquireResult tunerAcquire;

                    if (TryGetHdHomeRunTunerIndex(context, out var tunerIdx))
                    {
                        if (!hdHomeRunTunerManager.IsValidTunerIndex(tunerIdx))
                        {
                            logger.LogWarning(
                                "HDHR tuner request rejected: tuner index out of range. path={Path} tuner={Tuner} client={Client}",
                                context.Request.Path.Value,
                                tunerIdx,
                                context.Connection.RemoteIpAddress);
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            return;
                        }

                        var tunerId = HdHomeRunTunerManager.FormatTunerId(tunerIdx);
                        tunerAcquire = hdHomeRunTunerManager.Acquire(tunerId, streamKey);
                    }
                    else
                    {
                        tunerAcquire = hdHomeRunTunerManager.AcquireAuto(streamKey);
                    }

                    if (!tunerAcquire.Succeeded || tunerAcquire.Reservation is null)
                    {
                        var leases = hdHomeRunTunerManager.GetActiveLeases();
                        logger.LogWarning(
                            "HDHR tuner acquire rejected: path={Path} streamKey={StreamKey} client={Client} reason={Reason} activeLeaseCount={LeaseCount} activeLeases={Leases}",
                            context.Request.Path.Value,
                            streamKey,
                            context.Connection.RemoteIpAddress,
                            tunerAcquire.Error ?? "unknown",
                            leases.Count,
                            string.Join(",", leases.Select(x => $"{x.VirtualTunerId}:{x.StreamKey}:{x.ClientId ?? "n/a"}")));
                        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        context.Response.Headers["Retry-After"] = "30";
                        context.Response.Headers["X-HDHomeRun-Error"] = "805";
                        if (!string.IsNullOrWhiteSpace(tunerAcquire.Error))
                            await context.Response.WriteAsync(tunerAcquire.Error, cancellationToken);
                        return;
                    }

                    tunerReservation = tunerAcquire.Reservation;
                    logger.LogInformation(
                        "HDHR tuner acquired: path={Path} tunerId={TunerId} reservationId={ReservationId} streamKey={StreamKey} priorSubscriber={HadPriorSubscriber}",
                        context.Request.Path.Value,
                        tunerReservation.VirtualTunerId,
                        tunerReservation.ReservationId,
                        streamKey,
                        tunerAcquire.PriorSubscriber is not null);
                    if (tunerAcquire.PriorSubscriber is not null)
                        await tunerAcquire.PriorSubscriber.CompleteAsync(SubscriberDisconnectReason.Retuned);
                }

                var session = await channelSessionManager.GetOrCreateAsync(resolved.SourceDescriptor, cancellationToken);
                subscriber = await session.AttachSubscriberAsync(context, cancellationToken);
                generatedHlsSessionManager.NotifyDirectClientActive(
                    subscriber.RemoteIp,
                    subscriber.UserAgent,
                    session.SessionId);

                if (tunerReservation is not null)
                {
                    subscriber.AttachHdhrDiagnostics(new HdhrSubscriberDiagnostics(
                        tunerReservation.VirtualTunerId,
                        tunerReservation.ReservationId,
                        tunerReservation.StreamKey,
                        context.Request.Path.Value ?? resolved.SourceDescriptor.RequestedRoute));
                    tunerSessionRetention = session.RetainExternalActivity();
                    hdHomeRunTunerManager.Activate(
                        tunerReservation,
                        subscriber,
                        resolved.SourceDescriptor.DisplayName,
                        tunerSessionRetention);
                    tunerSessionRetention = null;
                    logger.LogInformation(
                        "HDHR tuner activated: tunerId={TunerId} reservationId={ReservationId} streamKey={StreamKey} channel={Channel} clientId={ClientId}",
                        tunerReservation.VirtualTunerId,
                        tunerReservation.ReservationId,
                        tunerReservation.StreamKey,
                        resolved.SourceDescriptor.DisplayName,
                        subscriber.ClientId);
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
                tunerSessionRetention?.Dispose();

                if (tunerReservation is not null)
                {
                    var hdhrDisconnectGrace = ResolveHdhrDisconnectGrace(streamProxyOptions.Value);
                    hdHomeRunTunerManager.ReleaseWithGrace(
                        tunerReservation.ReservationId,
                        hdhrDisconnectGrace,
                        subscriber?.ClientId);
                    var leases = hdHomeRunTunerManager.GetActiveLeases();
                    logger.LogInformation(
                        "HDHR tuner release scheduled: tunerId={TunerId} reservationId={ReservationId} streamKey={StreamKey} graceSeconds={GraceSeconds} activeLeaseCount={LeaseCount}",
                        tunerReservation.VirtualTunerId,
                        tunerReservation.ReservationId,
                        tunerReservation.StreamKey,
                        Math.Max(0, (int)Math.Ceiling(hdhrDisconnectGrace.TotalSeconds)),
                        leases.Count);
                }
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
        ChannelSessionManager channelSessionManager,
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
        StreamSourceDescriptor? sourceDescriptor = null;
        var useSharedSession = false;
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
            sourceDescriptor = resolved.SourceDescriptor;
            useSharedSession = resolved.UseSharedSession;
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

        // Only reserve an HLS slot for non-shared (native upstream HLS) sessions.
        // Shared sessions relay through the ring buffer and don't hold a separate provider slot.
        if (!useSharedSession && sourceDescriptor is not null)
        {
            try
            {
                _ = channelSessionManager.ReserveHlsSlot(sourceDescriptor);
                channelSessionManager.TouchHlsSlot(sourceDescriptor.SessionKey);
            }
            catch (StreamAdmissionException ex)
            {
                if (ex.RetryAfterSeconds is { } retry)
                    context.Response.Headers["Retry-After"] = retry.ToString();
                context.Response.StatusCode = ex.StatusCode;
                return;
            }
        }

        await hlsProxyService.ProxyAsync(context, upstreamUrl, segmentProxyBase, providerId, cancellationToken);
    }

    private static async Task ServeExternalHlsManifestAsync(
        string streamKey,
        HttpContext context,
        StreamRequestResolver streamRequestResolver,
        ChannelSessionManager channelSessionManager,
        GeneratedHlsSessionManager generatedHlsSessionManager,
        InternalRelaySecretService internalRelaySecretService,
        HlsManifestRewriter hlsManifestRewriter,
        IOptions<StreamProxyOptions> streamProxyOptions,
        ILoggerFactory loggerFactory,
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

        if (!resolved.IsSuccess || resolved.Entry is null || resolved.SourceDescriptor is null)
        {
            context.Response.StatusCode = resolved.FailureStatusCode ?? StatusCodes.Status404NotFound;
            if (!string.IsNullOrWhiteSpace(resolved.FailureMessage))
                await context.Response.WriteAsync(resolved.FailureMessage, cancellationToken);
            return;
        }

        if (!streamProxyOptions.Value.StreamingEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Stream proxy is disabled.", cancellationToken);
            return;
        }

        var entry = resolved.Entry;
        var source = resolved.SourceDescriptor;

        var parentSession = await channelSessionManager.TryGetOrCreateForGeneratedHlsAsync(
            source, useSharedSession: true, cancellationToken);

        string generatedStreamUrl = source.StreamUrl;
        string? generatedRelaySecret = null;
        string? parentStreamSessionId = null;

        if (parentSession is not null)
        {
            var sk = source.SessionKey;
            parentStreamSessionId = parentSession.SessionId;
            generatedStreamUrl =
                $"http://127.0.0.1:{context.Connection.LocalPort}/internal/relay/{Uri.EscapeDataString(sk.ProviderId)}/{Uri.EscapeDataString(sk.ProviderChannelId)}.ts";
            generatedRelaySecret = internalRelaySecretService.Secret;
        }

        var generatedSession = await generatedHlsSessionManager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: generatedStreamUrl,
                DisplayName: source.DisplayName,
                ProviderId: generatedRelaySecret is null ? source.ProviderId : null,
                AdmissionKey: source.SessionKey,
                InternalRelaySecret: generatedRelaySecret,
                ParentStreamSessionId: parentStreamSessionId,
                RequestedRoute: context.Request.Path.Value ?? "/hls/manifest"),
            cancellationToken);

        if (generatedSession is null)
        {
            logger.LogWarning(
                "External HLS startup failed: channel={Channel} key={StreamKey}",
                entry.DisplayName,
                streamKey);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Append("Retry-After", "5");
            await context.Response.WriteAsync("HLS unavailable for this channel.", cancellationToken);
            return;
        }

        generatedHlsSessionManager.TrackClient(
            generatedSession.SessionId,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString(),
            context.Request.Path.Value ?? string.Empty,
            GeneratedHlsSessionManager.ShouldCountAsViewer(context.Request.Headers.UserAgent.ToString()));

        var manifest = await generatedHlsSessionManager.ReadManifestAsync(generatedSession.SessionId, cancellationToken);
        if (manifest is null)
        {
            logger.LogDebug(
                "External HLS manifest not ready: channel={Channel} key={StreamKey} session={SessionId}",
                entry.DisplayName,
                streamKey,
                generatedSession.SessionId);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Append("Retry-After", "2");
            await context.Response.WriteAsync("HLS manifest not ready yet.", cancellationToken);
            return;
        }

        var manifestUrl = new Uri($"{GetBaseUrl(context)}{context.Request.Path}");
        var segmentBase = $"{GetBaseUrl(context)}/hls/generated/{Uri.EscapeDataString(generatedSession.SessionId)}";

        var rewritten = hlsManifestRewriter.Rewrite(
            manifest,
            manifestUrl,
            uri =>
            {
                var fileName = Path.GetFileName(uri.AbsolutePath);
                if (string.IsNullOrWhiteSpace(fileName))
                    return uri.ToString();
                var segmentUrl = $"{segmentBase}/{Uri.EscapeDataString(fileName)}";
                return segmentUrl.ApplyClientAccessQuery(context);
            });

        logger.LogInformation(
            "External HLS manifest served: channel={Channel} key={StreamKey} session={SessionId}",
            entry.DisplayName,
            streamKey,
            generatedSession.SessionId);

        context.Response.ContentType = "application/vnd.apple.mpegurl";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.WriteAsync(rewritten, cancellationToken);
    }

    private static async Task ServeGeneratedHlsAssetAsync(
        string sessionId,
        string asset,
        HttpContext context,
        GeneratedHlsSessionManager generatedHlsSessionManager,
        HlsManifestRewriter hlsManifestRewriter,
        CancellationToken cancellationToken)
    {
        if (!generatedHlsSessionManager.TryResolveAssetPath(sessionId, asset, out var filePath, out var contentType))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        generatedHlsSessionManager.TrackClient(
            sessionId,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString(),
            context.Request.Path.Value ?? string.Empty,
            GeneratedHlsSessionManager.ShouldCountAsViewer(context.Request.Headers.UserAgent.ToString()));

        if (contentType.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase))
        {
            string manifest;
            try
            {
                manifest = await File.ReadAllTextAsync(filePath, cancellationToken);
            }
            catch
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var manifestUrl = new Uri($"{GetBaseUrl(context)}{context.Request.Path}");
            var generatedAssetBase = $"{GetBaseUrl(context)}/hls/generated/{Uri.EscapeDataString(sessionId)}";

            var rewritten = hlsManifestRewriter.Rewrite(
                manifest,
                manifestUrl,
                uri =>
                {
                    var fileName = Path.GetFileName(uri.AbsolutePath);
                    if (string.IsNullOrWhiteSpace(fileName))
                        return uri.ToString();

                    var generatedAssetUrl = $"{generatedAssetBase}/{Uri.EscapeDataString(fileName)}";
                    return generatedAssetUrl.ApplyClientAccessQuery(context);
                });

            context.Response.ContentType = contentType;
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.WriteAsync(rewritten, cancellationToken);
            return;
        }

        context.Response.ContentType = contentType;
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.SendFileAsync(filePath, cancellationToken);
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
            var activeProfileId = await db.Profiles.AsNoTracking()
                .Where(x => x.IsActive)
                .Select(x => x.ProfileId)
                .FirstOrDefaultAsync(cancellationToken);
            if (activeProfileId is null) return null;
            profileProvider = await db.ProfileProviders.AsNoTracking()
                .Where(x => x.ProfileId == activeProfileId && x.Enabled)
                .OrderBy(x => x.Priority)
                .FirstOrDefaultAsync(cancellationToken);
            if (profileProvider is null) return null;
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
        var activeProfileId = await db.Profiles.AsNoTracking()
            .Where(x => x.IsActive && x.Enabled)
            .Select(x => x.ProfileId)
            .FirstOrDefaultAsync(cancellationToken);

        if (activeProfileId is null)
        {
            reasons.Add("no active profile");
        }
        else
        {
            var hasActiveSnapshot = await db.Snapshots.AsNoTracking()
                .AnyAsync(x => x.ProfileId == activeProfileId && x.Status == "active", cancellationToken);
            if (!hasActiveSnapshot)
                reasons.Add("no active snapshot for active profile");
        }

        if (refreshTrigger.IsRefreshing && reasons.Count > 0)
            reasons.Add("refresh in progress");

        var reason = string.Join("; ", reasons);

        return reasons.Count == 0
            ? Results.Ok(new { ready = true })
            : Results.Json(new { ready = false, reason, reasons }, statusCode: StatusCodes.Status503ServiceUnavailable);
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

    private static async Task<IResult> ServeDebugStreamResetAsync(
        ChannelSessionManager sessionManager,
        StreamingDiagnosticsStore diagnosticsStore)
    {
        await sessionManager.ResetAllAsync();
        diagnosticsStore.Clear();
        return Results.Ok(new { cleared = true });
    }

    private static IResult ServeDebugStrikeResetAsync(UpstreamFailureStrikeStore strikeStore)
    {
        strikeStore.ClearAll();
        return Results.Ok(new { cleared = true });
    }

    private static async Task<IResult> ServeDebugSnapshotIdleAsync(
        IRefreshTrigger refreshTrigger,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var latestRun = await db.FetchRuns
            .AsNoTracking()
            .Where(x => x.Type == "snapshot")
            .OrderByDescending(x => x.StartedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return Results.Json(new
        {
            idle = !refreshTrigger.IsRefreshing,
            running = refreshTrigger.IsRefreshing,
            lastStatus = latestRun?.Status,
            startedUtc = latestRun?.StartedUtc,
            completedUtc = latestRun?.FinishedUtc,
        }, JsonOptions);
    }

    private static async Task<IResult> ServeDebugStreamHealthSeedAsync(
        StreamHealthSeedRequest request,
        ApplicationDbContext db,
        IStreamChannelHealthProfileService healthProfileService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderId)
            || string.IsNullOrWhiteSpace(request.ProviderChannelId)
            || string.IsNullOrWhiteSpace(request.DisplayName)
            || request.Events is null
            || request.Events.Count == 0)
        {
            return Results.BadRequest(new
            {
                error = "providerId, providerChannelId, displayName, and at least one event are required.",
            });
        }

        var rows = new List<StreamChannelHealthEvent>(request.Events.Count);
        foreach (var seededEvent in request.Events)
        {
            if (!Enum.TryParse<StreamDiagnosticEventKind>(seededEvent.EventKind, ignoreCase: true, out var eventKind))
            {
                return Results.BadRequest(new
                {
                    error = $"Unknown stream health event kind '{seededEvent.EventKind}'.",
                });
            }

            rows.Add(new StreamChannelHealthEvent
            {
                StreamChannelHealthEventId = Guid.NewGuid().ToString("N"),
                ProviderId = request.ProviderId,
                ProviderChannelId = request.ProviderChannelId,
                DisplayName = request.DisplayName,
                EventKind = eventKind.ToString(),
                EventUtc = (seededEvent.EventUtc ?? DateTimeOffset.UtcNow).UtcDateTime,
                SessionId = seededEvent.SessionId,
                RelayMode = seededEvent.RelayMode,
                RouteClassification = "debug_seed",
                UpstreamFailureKind = seededEvent.UpstreamFailureKind,
                ReconnectAttempt = seededEvent.ReconnectAttempt,
                SafeStartKind = seededEvent.SafeStartKind,
                ClientAbortAfterRecovery = seededEvent.ClientAbortAfterRecovery
                    || eventKind == StreamDiagnosticEventKind.ClientAbortAfterRecovery,
                ForcedRetune = seededEvent.ForcedRetune
                    || eventKind is StreamDiagnosticEventKind.RecoveryForcedRetune
                        or StreamDiagnosticEventKind.ControlledDownstreamRetune,
                TsSyncLoss = seededEvent.TsSyncLoss
                    || eventKind == StreamDiagnosticEventKind.MpegTsSyncLost,
                CleanWatchDurationMs = seededEvent.CleanWatchDurationSeconds is { } cleanWatchSeconds
                    ? Math.Max(0, cleanWatchSeconds) * 1000
                    : null,
            });
        }

        db.StreamChannelHealthEvents.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        healthProfileService.Invalidate(request.ProviderId, request.ProviderChannelId);
        return Results.Ok(new
        {
            seeded = rows.Count,
            request.ProviderId,
            request.ProviderChannelId,
        });
    }

    private static async Task<IResult> ServeDebugStreamHealthDeleteAsync(
        string providerId,
        string providerChannelId,
        ApplicationDbContext db,
        IStreamChannelHealthProfileService healthProfileService,
        CancellationToken cancellationToken)
    {
        var deleted = await db.StreamChannelHealthEvents
            .Where(e => e.ProviderId == providerId && e.ProviderChannelId == providerChannelId)
            .ExecuteDeleteAsync(cancellationToken);
        healthProfileService.Invalidate(providerId, providerChannelId);
        return Results.Ok(new { cleared = true, deleted, providerId, providerChannelId });
    }

    private static async Task<IResult> ServeDebugStreamHealthAsync(
        string providerId,
        string providerChannelId,
        IStreamChannelHealthProfileService healthProfileService,
        IOptions<ReconnectOptions> reconnectOptions,
        CancellationToken cancellationToken)
    {
        var evidence = await healthProfileService.GetEvidenceAsync(
            providerId,
            providerChannelId,
            reconnectOptions.Value,
            cancellationToken);
        return Results.Json(evidence, JsonOptions);
    }

    private static async Task ServeInternalRelayAsync(
        string providerId,
        string channelId,
        HttpContext context,
        InternalRelaySecretService secretService,
        ChannelSessionManager channelSessionManager,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("M3Undle.Web.Api.CompatibilityEndpoints");

        if (!context.Request.Headers.TryGetValue("X-M3Undle-Internal-Relay", out var headerValue)
            || headerValue.ToString() != secretService.Secret)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var key = new ChannelSessionKey(providerId, channelId);
        if (!channelSessionManager.TryGet(key, out var session) || session is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        try
        {
            var subscriber = await session.AttachSubscriberAsync(context, cancellationToken, isInternal: true);
            await subscriber.Completion;
        }
        catch (StreamAdmissionException ex)
        {
            logger.LogWarning(
                "Internal relay admission rejected for {ProviderId}/{ChannelId}: {Reason}",
                providerId, channelId, ex.Message);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = ex.StatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Internal relay failed for {ProviderId}/{ChannelId}.",
                providerId, channelId);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }

    private static async Task ServeStatusAsync(
        HttpContext context,
        LineupStatusService lineupStatusService,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await lineupStatusService.GetStatusAsync(cancellationToken);
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

    private static IResult ServeStreamsEventsStatusAsync(
        HttpContext context,
        StreamingDiagnosticsStore diagnosticsStore)
        => Results.Json(QueryDiagnosticEvents(context, diagnosticsStore), JsonOptions);

    private static IResult ServeStreamsSingleSessionEventsStatusAsync(
        string sessionId,
        HttpContext context,
        StreamingDiagnosticsStore diagnosticsStore)
        => Results.Json(QueryDiagnosticEvents(context, diagnosticsStore, sessionId), JsonOptions);

    // `/tune/*` exists as the legacy HDHomeRun alias for `/hdhr/tune/*`.
    // `/tuner{n}/*` is the native HDHomeRun route shape used by LibHDHomeRun clients.
    // `/auto/*` and `/hdhr/auto/*` are auto-tune routes that pick the first free tuner.
    private static bool IsHdHomeRunTuneRoute(PathString path)
        => path.StartsWithSegments("/hdhr/tune", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/tune", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/hdhr/auto", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/auto", StringComparison.OrdinalIgnoreCase)
           || IsNativeHdhrTunerPath(path);

    private static bool IsNativeHdhrTunerPath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.StartsWith("/hdhr/tuner", StringComparison.OrdinalIgnoreCase)
            && value.Length > "/hdhr/tuner".Length
            && char.IsDigit(value["/hdhr/tuner".Length]))
            return true;

        return value.StartsWith("/tuner", StringComparison.OrdinalIgnoreCase)
               && value.Length > "/tuner".Length
               && char.IsDigit(value["/tuner".Length]);
    }

    private static string ResolveHdHomeRunVirtualTunerId(HttpContext context)
    {
        if (!TryGetHdHomeRunTunerIndex(context, out var tunerIndex))
            return "auto";

        return HdHomeRunTunerManager.FormatTunerId(tunerIndex);
    }

    private static bool TryGetHdHomeRunTunerIndex(HttpContext context, out int tunerIndex)
    {
        tunerIndex = 0;
        if (!context.Request.RouteValues.TryGetValue("tuner", out var tunerValue))
            return false;

        return int.TryParse(
                   Convert.ToString(tunerValue, CultureInfo.InvariantCulture),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out tunerIndex)
               && tunerIndex >= 0;
    }

    private static string NormalizeHdhrGuideNumber(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("ch", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];

        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[1..];

        return normalized;
    }

    private static TimeSpan ResolveHdhrDisconnectGrace(StreamProxyOptions options)
    {
        var minimumGrace = TimeSpan.FromSeconds(90);
        var configuredGrace = options.IdleGrace < minimumGrace ? minimumGrace : options.IdleGrace;
        if (options.IdleGraceHardCap > TimeSpan.Zero && configuredGrace > options.IdleGraceHardCap)
            return options.IdleGraceHardCap;

        return configuredGrace;
    }

    private static IResult ServeStreamsSingleSessionStatusAsync(string sessionId, StreamingRegistry registry)
    {
        var snapshot = registry.TryGetSession(sessionId);
        return snapshot is null
            ? TypedResults.NotFound()
            : Results.Json(snapshot, JsonOptions);
    }

    private static IResult ServeDebugStreamRcaAsync(
        StreamingRegistry registry,
        StreamingDiagnosticsStore diagnosticsStore,
        UpstreamFailureStrikeStore strikeStore)
    {
        var cooldowns = strikeStore.GetActiveCooldowns()
            .Select(c => new StreamCooldownDiagnostic(
                c.Key.ToString(),
                c.Key.ProviderId,
                c.Key.ProviderChannelId,
                Math.Round(c.Remaining.TotalSeconds, 1)))
            .ToArray();

        return Results.Json(new StreamRcaBundle(
            GeneratedUtc: DateTimeOffset.UtcNow,
            ActiveSessions: registry.GetActiveSessions(),
            RecentEndedSessions: registry.GetRecentEndedSessions(),
            ActiveClients: registry.GetActiveClients(),
            ActiveProviderStreams: registry.GetActiveProviderStreams(),
            ActiveCooldowns: cooldowns,
            RecentEvents: diagnosticsStore.Query()), JsonOptions);
    }

    private static IReadOnlyList<StreamDiagnosticEvent> QueryDiagnosticEvents(
        HttpContext context,
        StreamingDiagnosticsStore diagnosticsStore,
        string? sessionId = null)
    {
        var query = context.Request.Query;
        var providerId = query.TryGetValue("providerId", out var providerValues)
            ? providerValues.ToString()
            : null;
        var providerChannelId = query.TryGetValue("providerChannelId", out var channelValues)
            ? channelValues.ToString()
            : null;
        StreamDiagnosticEventKind? kind = null;
        if (query.TryGetValue("kind", out var kindValues)
            && Enum.TryParse<StreamDiagnosticEventKind>(kindValues.ToString(), ignoreCase: true, out var parsedKind))
        {
            kind = parsedKind;
        }

        DateTimeOffset? sinceUtc = null;
        if (query.TryGetValue("sinceUtc", out var sinceValues)
            && DateTimeOffset.TryParse(
                sinceValues.ToString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedSince))
        {
            sinceUtc = parsedSince;
        }

        return diagnosticsStore.Query(sessionId, providerId, providerChannelId, kind, sinceUtc);
    }

    private sealed record StreamStatusSummary(
        int ActiveSessionCount,
        int ActiveSubscriberCount,
        int SessionsReconnecting,
        int TotalReconnectAttempts,
        IReadOnlyList<StreamSessionSnapshot> ActiveSessions,
        IReadOnlyList<StreamSessionSnapshot> RecentEndedSessions);

    private sealed record StreamCooldownDiagnostic(
        string SessionKey,
        string ProviderId,
        string ProviderChannelId,
        double RemainingSeconds);

    private sealed record StreamRcaBundle(
        DateTimeOffset GeneratedUtc,
        IReadOnlyList<StreamSessionSnapshot> ActiveSessions,
        IReadOnlyList<StreamSessionSnapshot> RecentEndedSessions,
        IReadOnlyList<StreamClientSnapshot> ActiveClients,
        IReadOnlyList<StreamProviderSnapshot> ActiveProviderStreams,
        IReadOnlyList<StreamCooldownDiagnostic> ActiveCooldowns,
        IReadOnlyList<StreamDiagnosticEvent> RecentEvents);

    private sealed record StreamHealthSeedRequest(
        string ProviderId,
        string ProviderChannelId,
        string DisplayName,
        IReadOnlyList<StreamHealthSeedEvent> Events);

    private sealed record StreamHealthSeedEvent(
        string EventKind,
        DateTimeOffset? EventUtc = null,
        string? SessionId = null,
        string? RelayMode = null,
        string? UpstreamFailureKind = null,
        int? ReconnectAttempt = null,
        string? SafeStartKind = null,
        bool ClientAbortAfterRecovery = false,
        bool ForcedRetune = false,
        bool TsSyncLoss = false,
        double? CleanWatchDurationSeconds = null);
}
