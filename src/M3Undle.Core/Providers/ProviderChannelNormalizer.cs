using System.Text.RegularExpressions;
using M3Undle.Core.M3u;

namespace M3Undle.Core.Providers;

public static class ProviderChannelNormalizer
{
    private static readonly Regex MetadataAttributeRegex =
        new("(?<key>[A-Za-z0-9\\-]+)=\"(?<value>[^\"]*)\"", RegexOptions.Compiled);

    public static NormalizedProviderChannel ParseEntry(M3uEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var metadata = entry.MetadataLines.FirstOrDefault() ?? string.Empty;
        var attributes = MetadataAttributeRegex.Matches(metadata)
            .Select(match => (Key: match.Groups["key"].Value, Value: match.Groups["value"].Value))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Value, StringComparer.OrdinalIgnoreCase);

        attributes.TryGetValue("tvg-id", out var tvgId);
        attributes.TryGetValue("tvg-name", out var tvgName);
        attributes.TryGetValue("tvg-logo", out var logoUrl);
        attributes.TryGetValue("group-title", out var groupTitleAttr);

        var groupTitle = !string.IsNullOrWhiteSpace(entry.Group)
            ? entry.Group!.Trim()
            : string.IsNullOrWhiteSpace(groupTitleAttr) ? null : groupTitleAttr.Trim();

        var displayName = string.IsNullOrWhiteSpace(entry.Title)
            ? (string.IsNullOrWhiteSpace(tvgName) ? "Unnamed Channel" : tvgName.Trim())
            : entry.Title.Trim();

        return new NormalizedProviderChannel
        {
            ProviderChannelKey = NormalizeProviderChannelKey(tvgId),
            DisplayName = displayName,
            TvgId = string.IsNullOrWhiteSpace(tvgId) ? null : tvgId.Trim(),
            TvgName = string.IsNullOrWhiteSpace(tvgName) ? null : tvgName.Trim(),
            LogoUrl = string.IsNullOrWhiteSpace(logoUrl) ? null : logoUrl.Trim(),
            StreamUrl = NormalizeStreamUrl(entry.Url?.Trim() ?? string.Empty),
            GroupTitle = groupTitle,
        };
    }

    public static string? NormalizeProviderChannelKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string NormalizeStreamUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        // Some providers emit https:// URLs on port 80 (the plain HTTP port).
        // .NET HttpClient will attempt TLS on port 80 and fail immediately.
        // Downgrade the scheme to http so the relay can connect.
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Port == 80)
        {
            return string.Concat("http://", url.AsSpan("https://".Length));
        }

        return url;
    }
}
