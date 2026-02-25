namespace M3Undle.Core.Net;

/// <summary>
/// Redacts sensitive information from URLs for safe logging.
/// </summary>
public static class UrlRedactor
{
    /// <summary>
    /// Redacts query string parameters that may contain sensitive information.
    /// Returns only the scheme, host, port, and path.
    /// </summary>
    public static string RedactUrl(Uri uri)
    {
        if (uri == null)
        {
            return string.Empty;
        }

        // Return only scheme://host:port/path (no query string or fragment)
        return uri.GetLeftPart(UriPartial.Path);
    }

    /// <summary>
    /// Redacts query string parameters that may contain sensitive information.
    /// Returns only the scheme, host, port, and path.
    /// </summary>
    public static string RedactUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return RedactUrl(uri);
        }

        // If not a valid URI, return as-is (probably a file path)
        return url;
    }
}

