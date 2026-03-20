using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Security;
using M3Undle.Web.Streaming.Compatibility;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Streaming.Resolution;
using M3Undle.Web.Streaming.Sessions;
using M3Undle.Web.Streaming.Subscribers;
using M3Undle.Web.Streaming.Upstream;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Api;

/// <summary>
/// Implements the Xtream Codes-compatible API surface:
///   GET/POST /player_api.php   — account info, category lists, stream lists
///   GET      /get.php          — M3U playlist (Xtream-style URL)
///   GET      /live/{user}/{pass}/{id}[/{*tail}]
///   GET      /movie/{user}/{pass}/{id}[/{*tail}]
///   GET      /series/{user}/{pass}/{id}[/{*tail}]
///
/// Clients such as TiviMate, GSE Player, and IPTV Smarters can connect using
/// these endpoints with the endpoint-security username and password.
/// </summary>
public static class XtreamEndpoints
{
    // snake_case JSON to match the Xtream Codes API wire format
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static IEndpointRouteBuilder MapXtreamEndpoints(this IEndpointRouteBuilder app)
    {
        // player_api.php and get.php use query-string auth — handled by MapClientSurface
        var client = app.MapClientSurface();
        client.MapGet("player_api.php", ServePlayerApiAsync);
        client.MapPost("player_api.php", ServePlayerApiAsync);
        client.MapGet("get.php", ServeGetM3uAsync);

        // Xtream path-embedded-credential streaming: /live/{user}/{pass}/{id}[.ext]
        // These use a dedicated filter that reads credentials from the route values.
        var xtream = app.MapGroup(string.Empty);
        xtream.AddEndpointFilter<XtreamPathCredentialFilter>();

        xtream.MapGet("live/{xtreamUser}/{xtreamPass}/{streamId}", ServeXtreamStreamAsync);
        xtream.MapGet("live/{xtreamUser}/{xtreamPass}/{streamId}/{*tail}", ServeXtreamStreamAsync);
        xtream.MapGet("movie/{xtreamUser}/{xtreamPass}/{streamId}", ServeXtreamStreamAsync);
        xtream.MapGet("movie/{xtreamUser}/{xtreamPass}/{streamId}/{*tail}", ServeXtreamStreamAsync);
        xtream.MapGet("series/{xtreamUser}/{xtreamPass}/{streamId}", ServeXtreamStreamAsync);
        xtream.MapGet("series/{xtreamUser}/{xtreamPass}/{streamId}/{*tail}", ServeXtreamStreamAsync);
        xtream.MapGet("hls/{xtreamUser}/{xtreamPass}/{streamKey}/proxy", ServeXtreamHlsProxyAsync);

        return app;
    }

    // -------------------------------------------------------------------------
    // player_api.php
    // -------------------------------------------------------------------------

    private static async Task<IResult> ServePlayerApiAsync(
        HttpContext context,
        ILineupRenderer lineupRenderer,
        XtreamStreamIdCache streamIdCache,
        CancellationToken cancellationToken)
    {
        var access = context.GetResolvedClientAccess();
        var action = context.Request.Query["action"].ToString();

        // No action or explicit get_account_info → return account + server info
        if (string.IsNullOrEmpty(action) || action == "get_account_info")
            return BuildAccountInfoResult(context, access);

        var lineup = await lineupRenderer.TryRenderActiveLineupAsync(
            access.Binding.ActiveProfileId, cancellationToken);

        if (lineup is null)
            return Results.Json(Array.Empty<object>(), JsonOptions);

        return action switch
        {
            "get_live_categories"   => BuildCategoriesResult(lineup, "live"),
            "get_vod_categories"    => BuildCategoriesResult(lineup, "vod"),
            "get_series_categories" => BuildCategoriesResult(lineup, "series"),
            "get_live_streams"      => BuildStreamsResult(context, lineup, "live"),
            "get_vod_streams"       => BuildStreamsResult(context, lineup, "vod"),
            "get_series"            => BuildStreamsResult(context, lineup, "series"),
            _                       => Results.Json(Array.Empty<object>(), JsonOptions),
        };
    }

    private static IResult BuildAccountInfoResult(HttpContext context, ResolvedClientAccess access)
    {
        var now = DateTimeOffset.UtcNow;

        var response = new
        {
            user_info = new
            {
                username = access.Credential.Username,
                password = access.UrlCredential?.Password ?? string.Empty,
                message = string.Empty,
                auth = 1,
                status = "Active",
                exp_date = (string?)null,
                is_trial = "0",
                active_cons = "0",
                created_at = now.ToUnixTimeSeconds().ToString(),
                max_connections = "1",
                allowed_output_formats = new[] { "ts", "m3u8" },
            },
            server_info = new
            {
                url = context.Request.Host.Host,
                port = (context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80)).ToString(),
                https_port = context.Request.IsHttps
                    ? (context.Request.Host.Port ?? 443).ToString()
                    : string.Empty,
                server_protocol = context.Request.Scheme,
                rtmp_port = "0",
                timezone = "UTC",
                timestamp_now = now.ToUnixTimeSeconds(),
                time_now = now.ToString("yyyy-MM-dd HH:mm:ss"),
                xui = true,
            },
        };

        return Results.Json(response, JsonOptions);
    }

    private static IResult BuildCategoriesResult(RenderedLineup lineup, string contentType)
    {
        var categories = lineup.Channels
            .Where(c => MatchesContentType(c.ContentType, contentType))
            .GroupBy(c => c.GroupTitle ?? "Uncategorized")
            .Select(g => new
            {
                category_id = CategoryId(g.Key).ToString(),
                category_name = g.Key,
                parent_id = 0,
            })
            .ToArray();

        return Results.Json(categories, JsonOptions);
    }

    private static IResult BuildStreamsResult(HttpContext context, RenderedLineup lineup, string contentType)
    {
        var access = context.GetResolvedClientAccess();
        var baseUrl = GetBaseUrl(context);
        var username = access.Credential.Username;
        var password = access.UrlCredential?.Password ?? string.Empty;
        var categoryFilter = context.Request.Query["category_id"].ToString();
        var added = ((DateTimeOffset)lineup.SnapshotCreatedUtc).ToUnixTimeSeconds().ToString();

        var channels = lineup.Channels
            .Where(c => MatchesContentType(c.ContentType, contentType));

        if (!string.IsNullOrEmpty(categoryFilter))
            channels = channels.Where(c => CategoryId(c.GroupTitle ?? "Uncategorized").ToString() == categoryFilter);

        var (segment, ext, streamType) = contentType switch
        {
            "vod"    => ("movie",  "mp4",  "movie"),
            "series" => ("series", "mkv",  "series"),
            _        => ("live",   "ts",   "live"),
        };

        var streams = channels
            .Select((c, i) =>
            {
                var streamId  = XtreamStreamIdCache.ToStreamId(c.StreamKey);
                var streamUrl = $"{baseUrl}/{segment}/{username}/{password}/{streamId}.{ext}";
                var catId     = CategoryId(c.GroupTitle ?? "Uncategorized").ToString();

                if (contentType == "live")
                {
                    return (object)new
                    {
                        num              = i + 1,
                        name             = c.DisplayName,
                        stream_type      = streamType,
                        stream_id        = streamId,
                        stream_icon      = c.LogoUrl ?? string.Empty,
                        epg_channel_id   = c.TvgId   ?? string.Empty,
                        added,
                        category_id      = catId,
                        custom_sid       = string.Empty,
                        tv_archive       = 0,
                        direct_source    = string.Empty,
                        tv_archive_duration = 0,
                    };
                }

                return (object)new
                {
                    num                 = i + 1,
                    name                = c.DisplayName,
                    stream_type         = streamType,
                    stream_id           = streamId,
                    stream_icon         = c.LogoUrl ?? string.Empty,
                    added,
                    category_id         = catId,
                    container_extension = ext,
                    custom_sid          = string.Empty,
                    direct_source       = string.Empty,
                };
            })
            .ToArray();

        return Results.Json(streams, JsonOptions);
    }

    // -------------------------------------------------------------------------
    // get.php — Xtream-style M3U playlist
    // -------------------------------------------------------------------------

    private static async Task ServeGetM3uAsync(
        HttpContext context,
        ILineupRenderer lineupRenderer,
        IM3USerializer m3uSerializer,
        CancellationToken cancellationToken)
    {
        var access = context.GetResolvedClientAccess();
        var lineup = await lineupRenderer.TryRenderActiveLineupAsync(
            access.Binding.ActiveProfileId, cancellationToken);

        if (lineup is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Append("Retry-After", "60");
            await context.Response.WriteAsync("No active snapshot available.", cancellationToken);
            return;
        }

        await m3uSerializer.WriteAsync(context, lineup, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Path-auth streaming: /live|movie|series/{user}/{pass}/{streamId}[.ext]
    // -------------------------------------------------------------------------

    private static async Task ServeXtreamStreamAsync(
        string streamId,
        HttpContext context,
        ApplicationDbContext db,
        ILineupRenderer lineupRenderer,
        XtreamStreamIdCache streamIdCache,
        StreamRequestResolver streamRequestResolver,
        ChannelSessionManager channelSessionManager,
        IHttpClientFactory httpClientFactory,
        HlsProxyService hlsProxyService,
        ILoggerFactory loggerFactory,
        IOptions<StreamProxyOptions> streamProxyOptions,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("M3Undle.XtreamStream");

        // Strip optional extension (.ts, .mp4, .mkv, etc.)
        var cleanId = streamId.Contains('.')
            ? streamId[..streamId.LastIndexOf('.')]
            : streamId;

        if (!int.TryParse(cleanId, out var numericId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Resolve streamKey from the numeric Xtream stream ID
        var access = context.GetResolvedClientAccess();
        var lineup = await lineupRenderer.TryRenderActiveLineupAsync(
            access.Binding.ActiveProfileId, cancellationToken);

        if (lineup is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Append("Retry-After", "60");
            return;
        }

        var streamKey = await streamIdCache.TryGetStreamKeyAsync(
            lineup.SnapshotId, lineup.ChannelIndexPath, numericId, cancellationToken);

        if (streamKey is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // From here the logic mirrors CompatibilityEndpoints.ServeStreamAsync
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
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Append("Retry-After", "60");
            await context.Response.WriteAsync("Stream proxy is disabled.", cancellationToken);
            return;
        }

        var entry = resolved.Entry;
        logger.LogInformation("Xtream stream tune-in: channel={Channel} id={StreamId} client={Client}",
            entry.DisplayName, streamId, context.Connection.RemoteIpAddress);

        if (resolved.UseSharedSession && resolved.SourceDescriptor is not null)
        {
            // Xtream clients request an explicit .ts extension for live streams.
            // Skip HLS only when the client is a native app AND has requested .ts explicitly.
            // Browser-based Xtream clients (IPTVnator, Electron apps) send a Mozilla User-Agent
            // and cannot play raw TS — they must receive HLS regardless of the URL extension.
            var isNativeAppTs = !IsBrowserClient(context)
                && streamId.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);

            var hlsCandidates = !isNativeAppTs
                ? HlsDetection.GetHlsCandidates(resolved.SourceDescriptor.StreamUrl)
                : [];
            if (hlsCandidates.Count > 0)
            {
                var xtreamUser = context.Request.RouteValues["xtreamUser"]?.ToString() ?? string.Empty;
                var xtreamPass = context.Request.RouteValues["xtreamPass"]?.ToString() ?? string.Empty;
                var segmentProxyBase =
                    $"{GetBaseUrl(context)}/hls/{Uri.EscapeDataString(xtreamUser)}/{Uri.EscapeDataString(xtreamPass)}/{Uri.EscapeDataString(streamKey)}/proxy";

                var manifest = await hlsProxyService.FetchAndRewriteManifestAsync(
                    hlsCandidates, resolved.SourceDescriptor, segmentProxyBase, cancellationToken);

                if (manifest is not null)
                {
                    logger.LogInformation(
                        "Xtream HLS delivery: channel={Channel} id={StreamId} segmentProxyBase={SegmentProxyBase}",
                        entry.DisplayName, streamId, segmentProxyBase);
                    context.Response.ContentType = "application/vnd.apple.mpegurl";
                    context.Response.Headers.CacheControl = "no-cache";
                    context.Response.Headers.AccessControlAllowOrigin = "*";
                    await context.Response.WriteAsync(manifest, cancellationToken);
                    return;
                }

                logger.LogInformation(
                    "HLS not available for '{Channel}', falling back to TS session.",
                    entry.DisplayName);
            }

            SubscriberConnection? subscriber = null;
            try
            {
                var session = await channelSessionManager.GetOrCreateAsync(
                    resolved.SourceDescriptor, cancellationToken);
                subscriber = await session.AttachSubscriberAsync(context, cancellationToken);
                await subscriber.Completion;
                return;
            }
            catch (StreamAdmissionException ex)
            {
                logger.LogWarning(
                    "Xtream shared stream admission rejected for {ProviderId}/{ProviderChannelId}: {Reason}",
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
                    "Xtream shared stream upstream failure for id={StreamId}. kind={FailureKind}",
                    streamId, ex.FailureKind);
                if (!context.Response.HasStarted)
                    context.Response.StatusCode = ex.FailureKind is UpstreamFailureKind.UpstreamAuth
                        or UpstreamFailureKind.UpstreamNotFound
                        or UpstreamFailureKind.StartupFatal
                        ? StatusCodes.Status502BadGateway
                        : StatusCodes.Status503ServiceUnavailable;
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Xtream shared stream delivery failed for id={StreamId}.", streamId);
                if (!context.Response.HasStarted)
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
        }

        await ServeDirectRelayAsync(context, db, httpClientFactory, logger, entry.StreamUrl, entry.DisplayName, cancellationToken);
    }

    private static async Task ServeXtreamHlsProxyAsync(
        string streamKey,
        HttpContext context,
        HlsProxyService hlsProxyService,
        ILoggerFactory loggerFactory,
        string? u,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("M3Undle.HlsProxy");

        context.Response.Headers.AccessControlAllowOrigin = "*";

        if (string.IsNullOrWhiteSpace(u))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
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
            return;
        }

        if (!Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        logger.LogDebug("HLS segment proxy: key={StreamKey} upstream={UpstreamUrl}", streamKey, upstreamUrl);

        var xtreamUser = context.Request.RouteValues["xtreamUser"]?.ToString() ?? string.Empty;
        var xtreamPass = context.Request.RouteValues["xtreamPass"]?.ToString() ?? string.Empty;
        var segmentProxyBase =
            $"{GetBaseUrl(context)}/hls/{Uri.EscapeDataString(xtreamUser)}/{Uri.EscapeDataString(xtreamPass)}/{Uri.EscapeDataString(streamKey)}/proxy";

        await hlsProxyService.ProxyAsync(context, upstreamUrl, segmentProxyBase, cancellationToken);
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
        var access = context.GetResolvedClientAccess();
        var profileProvider = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.ProfileId == access.Binding.ActiveProfileId && x.Enabled)
            .OrderBy(x => x.Priority)
            .FirstOrDefaultAsync(cancellationToken);

        var provider = profileProvider is not null
            ? await db.Providers.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProviderId == profileProvider.ProviderId && x.Enabled, cancellationToken)
            : await db.Providers.AsNoTracking()
                .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, cancellationToken);

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
                streamUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            context.Response.StatusCode = (int)upstreamResponse.StatusCode;

            if (upstreamResponse.Content.Headers.ContentType is not null)
                context.Response.ContentType = upstreamResponse.Content.Headers.ContentType.ToString();

            if (upstreamResponse.Content.Headers.ContentLength.HasValue)
                context.Response.ContentLength = upstreamResponse.Content.Headers.ContentLength.Value;

            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
            await upstreamStream.CopyToAsync(context.Response.Body, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Xtream stream client disconnected: channel={Channel}", displayName);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Xtream stream upstream request failed: channel={Channel}", displayName);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true when a channel's content type matches the requested Xtream category type.
    /// Live catches everything that isn't explicitly vod or series.
    /// </summary>
    private static bool MatchesContentType(string channelContentType, string requestedType) =>
        requestedType switch
        {
            "vod"    => channelContentType == "vod",
            "series" => channelContentType == "series",
            _        => channelContentType != "vod" && channelContentType != "series",
        };

    /// <summary>Stable 31-bit numeric category ID derived from the group title.</summary>
    private static int CategoryId(string groupTitle)
    {
        var bytes = Encoding.UTF8.GetBytes(groupTitle);
        var hash = MD5.HashData(bytes);
        var value = BitConverter.ToUInt32(hash, 0);
        return (int)(value & 0x7FFF_FFFF);
    }

    private static string GetBaseUrl(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}";

    /// <summary>
    /// Returns true when the request originates from a browser or Electron-based app.
    /// These clients cannot decode raw MPEG-TS and require HLS delivery.
    /// Native IPTV apps (TiviMate, IPTVator, Smarters) use non-browser User-Agent strings.
    /// </summary>
    private static bool IsBrowserClient(HttpContext context) =>
        context.Request.Headers.UserAgent.ToString()
            .Contains("Mozilla/", StringComparison.OrdinalIgnoreCase);
}
