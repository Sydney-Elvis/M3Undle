namespace M3Undle.Core.Env;

public static class UrlSubstitutor
{
    public static string? SubstituteCredentials(string? value, IReadOnlyDictionary<string, string> env, out List<string> replaced)
    {
        replaced = new List<string>();
        if (string.IsNullOrEmpty(value) || !value.Contains('%'))
            return value;

        var matches = System.Text.RegularExpressions.Regex.Matches(value, @"%(\w+)%");
        if (matches.Count == 0)
            return value;

        string result = value;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var varName = match.Groups[1].Value;
            if (!seen.Add(varName))
                continue;

            // OS env first (Docker/system vars take priority), then .env file fallback
            var resolved = Environment.GetEnvironmentVariable(varName)
                ?? (env.TryGetValue(varName, out var fileVal) ? fileVal : null);

            if (resolved is null)
                continue;

            result = result.Replace($"%{varName}%", resolved, StringComparison.OrdinalIgnoreCase);
            replaced.Add(varName.ToUpperInvariant());
        }

        return NormalizeUrl(result);
    }

    /// <summary>
    /// Normalizes URLs to fix common issues like double slashes in the path.
    /// Uses UriBuilder to properly handle URL construction and explicitly removes duplicate slashes.
    /// </summary>
    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }

        // Only normalize if it looks like a URL
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        // Try to parse as URI and normalize
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var builder = new UriBuilder(uri);
            
            // Explicitly collapse multiple slashes in the path
            // Note: We preserve the protocol's :// but fix path slashes
            if (builder.Path.Contains("//", StringComparison.Ordinal))
            {
                // Replace multiple consecutive slashes with a single slash
                while (builder.Path.Contains("//", StringComparison.Ordinal))
                {
                    builder.Path = builder.Path.Replace("//", "/", StringComparison.Ordinal);
                }
            }
            
            return builder.Uri.ToString();
        }

        return url;
    }
}

