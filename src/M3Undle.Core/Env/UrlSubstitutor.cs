namespace M3Undle.Core.Env;

public static class UrlSubstitutor
{
    public static string? SubstituteCredentials(string? value, IReadOnlyDictionary<string, string> env, out List<string> replaced)
    {
        replaced = new List<string>();
        if (string.IsNullOrEmpty(value) || env.Count == 0)
        {
            return value;
        }

        string result = value;
        foreach (var kvp in env)
        {
            var before = result;
            
            // Replace %KEY% with value (case-insensitive key matching)
            var pattern = $"%{kvp.Key}%";
            result = result.Replace(pattern, kvp.Value, StringComparison.OrdinalIgnoreCase);
            
            // Track if replaced
            if (!string.Equals(before, result, StringComparison.Ordinal))
            {
                replaced.Add(kvp.Key.ToUpperInvariant());
            }
        }

        // Normalize URL to fix common issues like double slashes
        result = NormalizeUrl(result);

        return result;
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

