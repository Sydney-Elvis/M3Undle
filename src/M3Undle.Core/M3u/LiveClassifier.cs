namespace M3Undle.Core.M3u;

public static class LiveClassifier
{
    public static bool IsLive(string? url) => ClassifyContent(url) == "live";

    /// <summary>Returns "live", "vod", or "series" based on URL structure.</summary>
    public static string ClassifyContent(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "live";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return ClassifyBySubstring(url);

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (string.Equals(seg, "series", StringComparison.OrdinalIgnoreCase))
                return "series";
            if (string.Equals(seg, "movie", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(seg, "movies", StringComparison.OrdinalIgnoreCase))
                return "vod";
        }

        var queryType = GetQueryTypeValue(uri.Query);
        if (queryType is not null)
        {
            if (string.Equals(queryType, "series", StringComparison.OrdinalIgnoreCase))
                return "series";
            if (string.Equals(queryType, "vod", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(queryType, "movie", StringComparison.OrdinalIgnoreCase))
                return "vod";
        }

        return "live";
    }

    private static string ClassifyBySubstring(string url)
    {
        if (url.Contains("/series/", StringComparison.OrdinalIgnoreCase))
            return "series";
        if (url.Contains("/movie/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/movies/", StringComparison.OrdinalIgnoreCase))
            return "vod";
        return "live";
    }

    private static string? GetQueryTypeValue(string query)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 0) continue;

            var key = Uri.UnescapeDataString(kv[0]);
            if (!string.Equals(key, "type", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "kind", StringComparison.OrdinalIgnoreCase))
                continue;

            return kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
        }

        return null;
    }
}
