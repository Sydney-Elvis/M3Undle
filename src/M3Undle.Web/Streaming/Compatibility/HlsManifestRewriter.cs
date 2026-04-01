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
    /// URI-bearing directive attributes (for example URI="...") are also rewritten.
    /// </summary>
    public string Rewrite(string content, Uri manifestBaseUri, Func<Uri, string> uriMapper)
    {
        var lines = content.Split('\n');
        var result = new StringBuilder(content.Length + 256);

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line))
            {
                result.Append(line).Append('\n');
                continue;
            }

            if (line.StartsWith('#'))
            {
                result.Append(RewriteDirectiveLine(line, manifestBaseUri, uriMapper)).Append('\n');
                continue;
            }

            var resolved = ResolveUri(line, manifestBaseUri);
            result.Append(resolved is not null ? uriMapper(resolved) : line).Append('\n');
        }

        return result.ToString();
    }

    private static string RewriteDirectiveLine(string line, Uri manifestBaseUri, Func<Uri, string> uriMapper)
    {
        if (!line.Contains("URI=", StringComparison.OrdinalIgnoreCase))
            return line;

        var output = new StringBuilder(line.Length + 64);
        var cursor = 0;

        while (cursor < line.Length)
        {
            var uriKeyIndex = line.IndexOf("URI=", cursor, StringComparison.OrdinalIgnoreCase);
            if (uriKeyIndex < 0)
            {
                output.Append(line, cursor, line.Length - cursor);
                break;
            }

            output.Append(line, cursor, uriKeyIndex - cursor);
            output.Append("URI=");

            var valueStart = uriKeyIndex + "URI=".Length;
            if (valueStart >= line.Length)
                break;

            if (line[valueStart] == '"')
            {
                var quotedEnd = line.IndexOf('"', valueStart + 1);
                if (quotedEnd < 0)
                {
                    output.Append(line, valueStart, line.Length - valueStart);
                    break;
                }

                var originalValue = line.Substring(valueStart + 1, quotedEnd - valueStart - 1);
                var rewritten = RewriteUriValue(originalValue, manifestBaseUri, uriMapper);
                output.Append('"').Append(rewritten).Append('"');
                cursor = quotedEnd + 1;
                continue;
            }

            var valueEnd = line.IndexOf(',', valueStart);
            if (valueEnd < 0)
                valueEnd = line.Length;

            var originalToken = line.Substring(valueStart, valueEnd - valueStart);
            var trimmedToken = originalToken.Trim();
            var rewrittenToken = RewriteUriValue(trimmedToken, manifestBaseUri, uriMapper);
            output.Append(rewrittenToken);
            cursor = valueEnd;
        }

        return output.ToString();
    }

    private static string RewriteUriValue(string candidate, Uri manifestBaseUri, Func<Uri, string> uriMapper)
    {
        var resolved = ResolveUri(candidate, manifestBaseUri);
        return resolved is null ? candidate : uriMapper(resolved);
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
