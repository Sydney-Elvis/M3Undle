using System.Reflection;

namespace M3Undle.Core;

public sealed record AppBuildInfo(string Version, string? BuildDateUtc, string? BuildNumber, string? GitHash)
{
    public static AppBuildInfo ForEntryAssembly()
    {
        return FromAssembly(Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly());
    }

    public static AppBuildInfo FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var version =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        string? buildDateUtc = null;
        string? buildNumber = null;
        string? gitHash = null;

        foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (buildDateUtc is null && string.Equals(attribute.Key, "BuildDateUtc", StringComparison.OrdinalIgnoreCase))
            {
                buildDateUtc = attribute.Value;
                continue;
            }

            if (buildNumber is null && string.Equals(attribute.Key, "BuildNumber", StringComparison.OrdinalIgnoreCase))
            {
                buildNumber = attribute.Value;
                continue;
            }

            if (gitHash is null && string.Equals(attribute.Key, "GitHash", StringComparison.OrdinalIgnoreCase))
            {
                gitHash = NormalizeGitHash(attribute.Value);
            }
        }

        return new AppBuildInfo(version, buildDateUtc, buildNumber, gitHash);
    }

    private static string? NormalizeGitHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var trimmed = value.Trim();
        return trimmed.Length > 7 ? trimmed[..7] : trimmed;
    }

    public string ToDisplayString()
    {
        if (!string.IsNullOrWhiteSpace(BuildNumber) && !string.IsNullOrWhiteSpace(BuildDateUtc))
            return $"{Version} (build {BuildNumber}, built {BuildDateUtc})";

        if (!string.IsNullOrWhiteSpace(BuildNumber))
            return $"{Version} (build {BuildNumber})";

        if (!string.IsNullOrWhiteSpace(BuildDateUtc))
            return $"{Version} (built {BuildDateUtc})";

        return Version;
    }
}
