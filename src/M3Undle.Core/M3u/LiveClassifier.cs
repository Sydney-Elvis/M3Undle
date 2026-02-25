namespace M3Undle.Core.M3u;

public static class LiveClassifier
{
    private static readonly string[] VodSegments = ["movie", "movies", "series"];

    public static bool IsLive(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // If we can't parse it as a URI, check if it contains VOD segments
            return !ContainsVodSegment(url);
        }

        // Check if the path contains movie/movies/series segments
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => VodSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        // If query contains type=vod or type=series, it's not live
        if (QueryContainsVod(uri.Query))
        {
            return false;
        }

        // Default: if it's not VOD, assume it's live
        return true;
    }

    private static bool ContainsVodSegment(string url)
    {
        return url.Contains("/movie/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/movies/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/series/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool QueryContainsVod(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return false;
        }

        var pairs = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(kv[0]);
            if (!string.Equals(key, "type", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "kind", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
            if (string.Equals(value, "vod", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "movie", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "series", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

