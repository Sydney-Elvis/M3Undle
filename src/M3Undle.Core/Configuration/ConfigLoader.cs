using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace M3Undle.Core.Configuration;

public static class ConfigLoader
{
    public static async Task<(ProfileConfig Profile, string ConfigDirectory)> LoadProfileAsync(
        string configPath,
        string profileName,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            throw new CliException($"Config file not found: {configPath}", ExitCodes.ConfigError);
        }

        var text = await File.ReadAllTextAsync(configPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new CliException($"Config file is empty: {configPath}", ExitCodes.ConfigError);
        }

        var config = ParseConfiguration(configPath, text);

        if (!config.Profiles.TryGetValue(profileName, out var profile))
        {
            throw new CliException($"Profile '{profileName}' not found in config.", ExitCodes.ConfigError);
        }

        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Environment.CurrentDirectory;
        return (profile, configDir);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", 
        Justification = "ParseYaml is only called for YAML files. Users are informed via attributes that JSON is the trim-compatible format.")]
    private static MediaConfig ParseConfiguration(string configPath, string rawText)
    {
        return IsYaml(configPath, rawText)
            ? ParseYaml(rawText)
            : ParseJson(rawText);
    }

    [RequiresUnreferencedCode("YAML deserialization uses reflection and is not trim-compatible. Consider using JSON configuration instead.")]
    [RequiresDynamicCode("YAML deserialization may require dynamic code generation.")]
    private static MediaConfig ParseYaml(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        try
        {
            var config = deserializer.Deserialize<MediaConfig>(new StringReader(yaml));
            return NormalizeConfig(config);
        }
        catch (Exception ex)
        {
            throw new CliException($"Failed to parse YAML config: {ex.Message}", ExitCodes.ConfigError);
        }
    }

    private static MediaConfig ParseJson(string json)
    {
        try
        {
            var config = JsonSerializer.Deserialize(json, MediaConfigJsonContext.Default.MediaConfig);
            return NormalizeConfig(config);
        }
        catch (Exception ex)
        {
            throw new CliException($"Failed to parse JSON config: {ex.Message}", ExitCodes.ConfigError);
        }
    }

    private static MediaConfig NormalizeConfig(MediaConfig? config)
    {
        if (config is null)
        {
            return new MediaConfig();
        }

        var profiles = config.Profiles ?? new Dictionary<string, ProfileConfig>();
        if (profiles.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            profiles = new Dictionary<string, ProfileConfig>(profiles, StringComparer.OrdinalIgnoreCase);
        }

        return new MediaConfig
        {
            Profiles = profiles,
        };
    }

    private static bool IsYaml(string path, string text)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".yml", StringComparison.OrdinalIgnoreCase) || ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = text.TrimStart();
        return trimmed.StartsWith("---", StringComparison.Ordinal) || trimmed.StartsWith("profiles:", StringComparison.OrdinalIgnoreCase);
    }
}

