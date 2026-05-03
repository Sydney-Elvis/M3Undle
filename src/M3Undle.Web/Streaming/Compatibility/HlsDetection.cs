namespace M3Undle.Web.Streaming.Compatibility;

/// <summary>
/// URL-shape and content-type heuristics for detecting HLS streams without making upstream requests.
/// </summary>
public static class HlsDetection
{
    /// <summary>Returns true if the Content-Type header indicates an HLS manifest.</summary>
    public static bool IsHlsContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        return contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the URL as a single-element list if it is an explicit .m3u8 URL, otherwise empty.
    /// Use this for source-driven relay decisions where heuristic candidate generation would
    /// incorrectly relay Xtream numeric URLs that already serve MPEG-TS directly.
    /// </summary>
    public static IReadOnlyList<string> GetExplicitHlsCandidates(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return [url];
        }

        return [];
    }

    /// <summary>
    /// Returns an ordered list of HLS manifest URL candidates to try for the given stream URL.
    /// Returns an empty list if no HLS variant can be inferred.
    /// Callers should try candidates in order and use the first that returns a valid M3U8.
    /// </summary>
    public static IReadOnlyList<string> GetHlsCandidates(string url)
    {
        // Already an HLS URL
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return [url];
        }

        if (uri is null) return [];

        var path = uri.AbsolutePath.TrimEnd('/');

        // .ts → .m3u8 (single candidate)
        if (path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            var newPath = string.Concat(path.AsSpan(0, path.Length - 3), ".m3u8");
            return [new UriBuilder(uri) { Path = newPath }.Uri.ToString()];
        }

        // Bare numeric ID with no extension — Xtream pattern: /{user}/{pass}/{id}
        // Try two candidates:
        //   1. /live/{user}/{pass}/{id}.m3u8  (standard Xtream HLS path)
        //   2. /{user}/{pass}/{id}.m3u8       (direct append, some providers)
        var lastSegment = path.Split('/').LastOrDefault();
        if (!string.IsNullOrEmpty(lastSegment)
            && !lastSegment.Contains('.')
            && lastSegment.All(char.IsAsciiDigit))
        {
            var segments = path.TrimStart('/');
            var hasLivePrefix = path.StartsWith("/live/", StringComparison.OrdinalIgnoreCase);

            if (!hasLivePrefix)
            {
                return
                [
                    new UriBuilder(uri) { Path = "/live/" + segments + ".m3u8" }.Uri.ToString(),
                    new UriBuilder(uri) { Path = path + ".m3u8" }.Uri.ToString(),
                ];
            }

            return [new UriBuilder(uri) { Path = path + ".m3u8" }.Uri.ToString()];
        }

        return [];
    }
}
