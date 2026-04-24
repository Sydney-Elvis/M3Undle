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
}
