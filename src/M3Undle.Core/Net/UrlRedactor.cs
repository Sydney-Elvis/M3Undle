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

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return builder.Uri.GetLeftPart(UriPartial.Path);
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

    public static string? RedactAbsoluteUrlOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return RedactUrl(uri);
    }
}
