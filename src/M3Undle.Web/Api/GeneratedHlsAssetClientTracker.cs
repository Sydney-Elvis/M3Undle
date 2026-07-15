using M3Undle.Web.Streaming.GeneratedHls;

namespace M3Undle.Web.Api;

/// <summary>
/// Shared client-tracking helpers for the generated-HLS asset routes exposed by both the Xtream
/// and compatibility endpoints. Each asset request (manifest or media segment) is reported to the
/// <see cref="GeneratedHlsSessionManager"/> so the Stream Monitor can classify per-client liveness;
/// only media-segment fetches count as media activity and contribute served bytes.
/// </summary>
internal static class GeneratedHlsAssetClientTracker
{
    /// <summary>
    /// Reports the current request to the session manager. <paramref name="mediaSegmentFetched"/>
    /// marks a media-segment (as opposed to manifest) fetch, and <paramref name="mediaBytesServed"/>
    /// is the segment size in bytes.
    /// </summary>
    public static void Track(
        GeneratedHlsSessionManager manager,
        string sessionId,
        HttpContext context,
        bool mediaSegmentFetched,
        long mediaBytesServed)
        => manager.TrackClient(
            sessionId,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString(),
            context.Request.Path.Value ?? string.Empty,
            GeneratedHlsSessionManager.ShouldCountAsViewer(context.Request.Headers.UserAgent.ToString()),
            mediaSegmentFetched: mediaSegmentFetched,
            mediaBytesServed: mediaBytesServed);

    /// <summary>
    /// Returns the on-disk length of a media-segment asset, or 0 for manifests and when the file
    /// cannot be stated. Segments are served whole, so the file length equals the bytes delivered.
    /// </summary>
    public static long GetAssetLength(string filePath, string contentType)
    {
        if (contentType.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase))
            return 0;

        try
        {
            return new FileInfo(filePath).Length;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
