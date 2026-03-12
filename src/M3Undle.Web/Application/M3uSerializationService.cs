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

        context.Response.ContentType = "application/x-mpegurl; charset=utf-8";
        await context.Response.WriteAsync(
            $"#EXTM3U url-tvg=\"{xmltvUrl}\" x-tvg-url=\"{xmltvUrl}\"\n",
            cancellationToken);

        var sb = new StringBuilder(512);
        foreach (var channel in lineup.Channels)
        {
            sb.Clear();
            sb.Append(BuildExtInf(channel));
            sb.Append('\n');
            sb.Append(BuildProxyStreamUrl(baseUrl, channel, context));
            sb.Append('\n');
            await context.Response.WriteAsync(sb.ToString(), cancellationToken);
        }
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
            url = $"{baseUrl}/{routeSegment}/{channel.StreamKey}";
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
