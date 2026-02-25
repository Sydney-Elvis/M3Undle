namespace M3Undle.Core.Env;

public static class EnvFileLoader
{
    public static IReadOnlyDictionary<string, string> LoadFromDirectory(string directory)
    {
        try
        {
            var path = Path.Combine(directory, ".env");
            if (!File.Exists(path))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var idx = trimmed.IndexOf('=');
                if (idx <= 0)
                {
                    continue;
                }

                var key = trimmed[..idx].Trim();
                var value = trimmed[(idx + 1)..].Trim();
                
                // Accept any key, not just USER and PASS
                if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key))
                {
                    map[key] = value;
                }
            }

            return map;
        }
        catch (Exception ex)
        {
            throw new CliException($"Failed to read .env file: {ex.Message}", ExitCodes.ConfigError);
        }
    }
}

