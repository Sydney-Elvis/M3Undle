namespace M3Undle.Core.Providers;

public static class XtreamProviderUrls
{
    public static string BuildPlaylistUrl(string baseUrl, string? username, string password)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(password);

        return $"{baseUrl}/get.php?username={Uri.EscapeDataString(username ?? string.Empty)}&password={Uri.EscapeDataString(password)}&type=m3u_plus&output=ts";
    }

    public static string BuildXmltvUrl(string baseUrl, string? username, string password)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(password);

        return $"{baseUrl}/xmltv.php?username={Uri.EscapeDataString(username ?? string.Empty)}&password={Uri.EscapeDataString(password)}";
    }

    public static string BuildPlayerApiUrl(string baseUrl, string? username, string password)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(password);

        return $"{baseUrl}/player_api.php?username={Uri.EscapeDataString(username ?? string.Empty)}&password={Uri.EscapeDataString(password)}";
    }

    /// <summary>
    /// Attempts to extract Xtream Codes credentials from a get.php-style M3U URL.
    /// Returns true when the URL is an Xtream endpoint with parseable credentials.
    /// </summary>
    public static bool TryExtractCredentials(
        string? url,
        out string baseUrl,
        out string username,
        out string password)
    {
        baseUrl = username = password = string.Empty;

        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme is not ("http" or "https"))
            return false;

        if (!uri.AbsolutePath.Contains("/get.php", StringComparison.OrdinalIgnoreCase))
            return false;

        var u = string.Empty;
        var p = string.Empty;
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0) continue;
            var key = Uri.UnescapeDataString(part[..eq]);
            var val = Uri.UnescapeDataString(part[(eq + 1)..]);
            if (key.Equals("username", StringComparison.OrdinalIgnoreCase)) u = val;
            else if (key.Equals("password", StringComparison.OrdinalIgnoreCase)) p = val;
        }

        if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(p))
            return false;

        username = u;
        password = p;
        baseUrl = uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        return true;
    }
}
