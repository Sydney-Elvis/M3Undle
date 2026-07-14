namespace M3Undle.Web.Streaming.Observability;

public sealed record ClientUserAgentIdentity(string DisplayName, int Specificity);

public static class ClientUserAgentResolver
{
    public static ClientUserAgentIdentity Resolve(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return new("Unknown", 0);

        if (userAgent.Contains("Mozilla/", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("Chrome/", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("Firefox/", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("Safari/", StringComparison.OrdinalIgnoreCase))
            return new("Browser", 1);

        if (userAgent.Contains("ExoPlayer", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("Media3", StringComparison.OrdinalIgnoreCase))
            return new("ExoPlayer", 1);

        if (userAgent.StartsWith("Dalvik", StringComparison.OrdinalIgnoreCase))
        {
            var platform = userAgent.Contains("TV", StringComparison.OrdinalIgnoreCase)
                ? "Android TV"
                : "Android";
            return new(platform, 1);
        }

        if (userAgent.StartsWith("okhttp", StringComparison.OrdinalIgnoreCase))
            return new("Android App", 1);

        var productName = ReadFirstProductName(userAgent);
        if (productName is null)
            return new("Unknown", 0);

        return new(productName, 2);
    }

    private static string? ReadFirstProductName(string userAgent)
    {
        var value = userAgent.AsSpan().TrimStart();
        if (value.IsEmpty || value[0] == '(')
            return null;

        var slashIndex = value.IndexOf('/');
        var length = slashIndex >= 0 ? slashIndex : 0;
        if (slashIndex < 0)
        {
            while (length < value.Length)
            {
                var character = value[length];
                if (char.IsWhiteSpace(character) || character == '(')
                    break;
                length++;
            }
        }

        var productName = value[..length].Trim();
        if (productName.IsEmpty || productName.IndexOf('(') >= 0)
            return null;

        const int maxDisplayLength = 40;
        return productName[..Math.Min(productName.Length, maxDisplayLength)].ToString();
    }
}
