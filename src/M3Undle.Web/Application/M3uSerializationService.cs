using System.Text;
using M3Undle.Web.Security;

namespace M3Undle.Web.Application;

public interface IM3USerializer
{
    Task WriteAsync(HttpContext context, RenderedLineup lineup, CancellationToken cancellationToken);
}

public sealed class M3uSerializer : IM3USerializer
{
    public async Task WriteAsync(HttpContext context, RenderedLineup lineup, CancellationToken cancellationToken)
    {
        var baseUrl = GetBaseUrl(context);
        var xmltvUrl = $"{baseUrl}/xmltv/m3undle.xml";
        xmltvUrl = xmltvUrl.ApplyClientAccessQuery(context);

        var payload = BuildPlaylist(lineup, baseUrl, xmltvUrl, context);

        context.Response.ContentType = "application/x-mpegurl; charset=utf-8";
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(payload);
        await context.Response.WriteAsync(payload, cancellationToken);
    }

    private static string BuildPlaylist(RenderedLineup lineup, string baseUrl, string xmltvUrl, HttpContext context)
    {
        // ASP.NET Core does not buffer response writes by default. Build the payload once so
        // M3U clients receive a normal-length response instead of thousands of tiny flushed chunks.
        var sb = new StringBuilder(Math.Max(512, lineup.Channels.Count * 160));
        sb.Append("#EXTM3U url-tvg=\"")
          .Append(xmltvUrl)
          .Append("\" x-tvg-url=\"")
          .Append(xmltvUrl)
          .Append("\"\n");

        foreach (var channel in lineup.Channels)
        {
            sb.Append(BuildExtInf(channel));
            sb.Append('\n');
            sb.Append(BuildProxyStreamUrl(baseUrl, channel, context));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string BuildExtInf(RenderedLineupChannel channel)
    {
        var sb = new StringBuilder("#EXTINF:-1");

        if (!string.IsNullOrWhiteSpace(channel.TvgId))
            sb.Append($" tvg-id=\"{channel.TvgId}\"");

        var tvgName = !string.IsNullOrWhiteSpace(channel.TvgName) ? channel.TvgName : channel.DisplayName;
        sb.Append($" tvg-name=\"{tvgName}\"");

        if (!string.IsNullOrWhiteSpace(channel.LogoUrl))
            sb.Append($" tvg-logo=\"{channel.LogoUrl}\"");

        if (!string.IsNullOrWhiteSpace(channel.GroupTitle))
            sb.Append($" group-title=\"{channel.GroupTitle}\"");

        if (channel.TvgChno.HasValue)
            sb.Append($" tvg-chno=\"{channel.TvgChno.Value}\"");

        sb.Append($",{channel.DisplayName}");
        return sb.ToString();
    }

    private static string BuildProxyStreamUrl(string baseUrl, RenderedLineupChannel channel, HttpContext context)
    {
        var routeSegment = channel.ContentType switch
        {
            "series" => "series",
            "vod" => "movie",
            _ => "live",
        };

        string url;
        if (routeSegment == "live")
        {
            var liveTail = BuildLivePlaybackTail(channel.StreamUrl);
            url = string.IsNullOrWhiteSpace(liveTail)
                ? $"{baseUrl}/{routeSegment}/{channel.StreamKey}"
                : $"{baseUrl}/{routeSegment}/{channel.StreamKey}/{Uri.EscapeDataString(liveTail)}";
            return url.ApplyClientAccessQuery(context);
        }

        var tail = GetUpstreamTailSegment(channel.StreamUrl);
        if (string.IsNullOrWhiteSpace(tail))
        {
            url = $"{baseUrl}/{routeSegment}/{channel.StreamKey}";
            return url.ApplyClientAccessQuery(context);
        }

        url = $"{baseUrl}/{routeSegment}/{channel.StreamKey}/{Uri.EscapeDataString(tail)}";
        return url.ApplyClientAccessQuery(context);
    }

    private static string? GetUpstreamTailSegment(string? streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
            return null;

        if (Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
        {
            var tail = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return string.IsNullOrWhiteSpace(tail) ? null : tail;
        }

        var fallback = streamUrl.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    private static string? BuildLivePlaybackTail(string? streamUrl)
    {
        var upstreamTail = GetUpstreamTailSegment(streamUrl);

        // Preserve HLS-looking tails so browser clients can still negotiate HLS.
        if (IsLikelyHlsStream(streamUrl))
            return upstreamTail;

        if (string.IsNullOrWhiteSpace(upstreamTail))
            return "stream.ts";

        if (upstreamTail.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            return upstreamTail;

        if (upstreamTail.Contains('.'))
            return "stream.ts";

        return upstreamTail + ".ts";
    }

    private static bool IsLikelyHlsStream(string? streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
            return false;

        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
            return streamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

        if (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(uri.Query))
            return false;

        var query = uri.Query.AsSpan().TrimStart('?').ToString();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 0)
                continue;

            var key = Uri.UnescapeDataString(kv[0]);
            if (!string.Equals(key, "type", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "output", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "format", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
            if (value.Contains("m3u8", StringComparison.OrdinalIgnoreCase)
                || value.Contains("hls", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
}
