using Microsoft.Extensions.Logging;
using YamlDotNet.RepresentationModel;

namespace M3Undle.Web.Application;

/// <summary>
/// Loads and parses config.yaml from M3UNDLE_CONFIG_DIR.
/// Extracts provider definitions for import into the service.
/// </summary>
public sealed class ConfigYamlService
{
    private readonly ILogger<ConfigYamlService> _logger;

    public ConfigYamlService(ILogger<ConfigYamlService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Loads config.yaml from M3UNDLE_CONFIG_DIR and extracts all provider definitions.
    /// </summary>
    public async Task<IReadOnlyList<ConfigYamlProvider>> LoadProvidersAsync()
    {
        var configDir = GetConfigDir();
        if (string.IsNullOrEmpty(configDir))
        {
            _logger.LogWarning("M3UNDLE_CONFIG_DIR not set; cannot load config.yaml");
            return [];
        }

        var configPath = Path.Combine(configDir, "config.yaml");
        if (!File.Exists(configPath))
        {
            _logger.LogWarning("config.yaml not found at {ConfigPath}", configPath);
            return [];
        }

        try
        {
            var content = await File.ReadAllTextAsync(configPath);
            return ParseYaml(content, configPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load config.yaml from {ConfigPath}", configPath);
            return [];
        }
    }

    /// <summary>
    /// Gets the path to config.yaml if it exists.
    /// </summary>
    public string? GetConfigYamlPath()
    {
        var configDir = GetConfigDir();
        if (string.IsNullOrEmpty(configDir))
            return null;

        var configPath = Path.Combine(configDir, "config.yaml");
        return File.Exists(configPath) ? configPath : null;
    }

    private IReadOnlyList<ConfigYamlProvider> ParseYaml(string content, string sourcePath)
    {
        var providers = new List<ConfigYamlProvider>();

        using var reader = new StringReader(content);
        var yaml = new YamlStream();
        yaml.Load(reader);

        if (yaml.Documents.Count == 0)
        {
            _logger.LogWarning("config.yaml contains no documents");
            return providers;
        }

        var root = (YamlMappingNode?)yaml.Documents[0].RootNode;
        if (root == null)
            return providers;

        // Support: profiles > <name> > inputs > playlist/epg
        var profilesKey = new YamlScalarNode("profiles");
        if (root.Children.TryGetValue(profilesKey, out var profilesValue) && profilesValue is YamlMappingNode profilesMap)
        {
            foreach (var profileEntry in profilesMap.Children)
            {
                var profileName = (profileEntry.Key as YamlScalarNode)?.Value;
                if (string.IsNullOrWhiteSpace(profileName) || profileEntry.Value is not YamlMappingNode profileNode)
                    continue;

                var provider = ParseProfileAsProvider(profileName, profileNode, sourcePath);
                if (provider != null)
                    providers.Add(provider);
            }
        }

        if (providers.Count == 0)
            _logger.LogWarning("No providers found in config.yaml");
        else
            _logger.LogInformation("Loaded {ProviderCount} providers from config.yaml", providers.Count);

        return providers;
    }

    private ConfigYamlProvider? ParseProfileAsProvider(string name, YamlMappingNode profileNode, string sourcePath)
    {
        var provider = new ConfigYamlProvider
        {
            Name = name,
            SourcePath = sourcePath,
        };

        var inputsKey = new YamlScalarNode("inputs");
        if (!profileNode.Children.TryGetValue(inputsKey, out var inputsValue) || inputsValue is not YamlMappingNode inputsNode)
        {
            _logger.LogWarning("Profile '{Name}' has no inputs section; skipping", name);
            return null;
        }

        var playlistKey = new YamlScalarNode("playlist");
        if (inputsNode.Children.TryGetValue(playlistKey, out var playlistValue) && playlistValue is YamlMappingNode playlistNode)
        {
            provider.PlaylistUrl = GetScalar(playlistNode, "url") ?? string.Empty;
        }

        var epgKey = new YamlScalarNode("epg");
        if (inputsNode.Children.TryGetValue(epgKey, out var epgValue) && epgValue is YamlMappingNode epgNode)
        {
            provider.XmltvUrl = GetScalar(epgNode, "url");
        }

        if (string.IsNullOrWhiteSpace(provider.PlaylistUrl))
        {
            _logger.LogWarning("Profile '{Name}' has no playlist URL; skipping", name);
            return null;
        }

        return provider;
    }

    private static string? GetScalar(YamlMappingNode node, string key)
    {
        var scalarKey = new YamlScalarNode(key);
        return node.Children.TryGetValue(scalarKey, out var value)
            ? (value as YamlScalarNode)?.Value
            : null;
    }

    private static string? GetConfigDir()
    {
        return Environment.GetEnvironmentVariable("M3UNDLE_CONFIG_DIR");
    }
}

/// <summary>
/// Represents a provider loaded from config.yaml.
/// </summary>
public sealed class ConfigYamlProvider
{
    public string Name { get; set; } = string.Empty;
    public string PlaylistUrl { get; set; } = string.Empty;
    public string? XmltvUrl { get; set; }
    public string? UserAgent { get; set; }
    public int TimeoutSeconds { get; set; } = 20;
    public Dictionary<string, string>? Headers { get; set; }
    public bool Enabled { get; set; } = true;
    public string SourcePath { get; set; } = string.Empty;
}

