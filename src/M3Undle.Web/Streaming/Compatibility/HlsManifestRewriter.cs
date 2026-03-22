using System.Text;

namespace M3Undle.Web.Streaming.Compatibility;

/// <summary>
/// Rewrites all segment and sub-playlist URIs in an HLS M3U8 manifest.
/// Handles both absolute and base-URI-relative segment references.
/// </summary>
public sealed class HlsManifestRewriter
{
    /// <summary>
    /// Rewrites every URI line in <paramref name="content"/>, resolving relative URIs
    /// against <paramref name="manifestBaseUri"/> and passing the resulting absolute URI
    /// to <paramref name="uriMapper"/> to produce the replacement string.
    /// Directive lines (starting with '#') and blank lines are passed through unchanged.
    /// </summary>
    public string Rewrite(string content, Uri manifestBaseUri, Func<Uri, string> uriMapper)
    {
        var lines = content.Split('\n');
        var result = new StringBuilder(content.Length + 256);

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                result.Append(line).Append('\n');
                continue;
            }

            var resolved = ResolveUri(line, manifestBaseUri);
            result.Append(resolved is not null ? uriMapper(resolved) : line).Append('\n');
        }

        return result.ToString();
    }

    private static Uri? ResolveUri(string uriString, Uri baseUri)
    {
        if (Uri.TryCreate(uriString, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme is "http" or "https")
                return absolute;

            // Xtream Codes panels emit file:// URIs in manifests (e.g. file:///hls/{hash}/{seg}.ts).
            // These are internal filesystem paths served via Nginx on the same origin.
            // Treat the path component as a server-relative path on the manifest's host.
            return new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.Port, absolute.AbsolutePath).Uri;
        }

        if (Uri.TryCreate(baseUri, uriString, out var relative))
            return relative;

        return null;
    }
}
