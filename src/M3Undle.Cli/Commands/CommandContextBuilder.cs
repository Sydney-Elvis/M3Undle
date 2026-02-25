using System.Diagnostics.CodeAnalysis;
using M3Undle.Core.Configuration;
using M3Undle.Core.Env;
using M3Undle.Core.Net;

namespace M3Undle.Cli.Commands;

public static class CommandContextBuilder
{
    private static readonly HashSet<string> GroupsAllowedOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "playlist-url", "config", "profile", "out-groups", "verbose", "live", "force"
    };
    private static readonly HashSet<string> RunAllowedOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "playlist-url", "epg-url", "groups-file", "out-playlist", "out-epg", "config", "profile", "verbose", "live"
    };

    [RequiresUnreferencedCode("Configuration loading may use YAML which requires reflection and is not trim-compatible.")]
    [RequiresDynamicCode("Configuration loading may use YAML which may require dynamic code generation.")]
    public static async Task<CommandContext> CreateAsync(
        CommandOptionSet options,
        CommandKind kind,
        TextWriter diagnostics,
        CancellationToken cancellationToken)
    {
        // Validate options
        var allowed = kind == CommandKind.Groups ? GroupsAllowedOptions : RunAllowedOptions;
        foreach (var opt in options.Keys)
        {
            if (!allowed.Contains(opt))
            {
                throw new CommandOptionException($"Unknown or unsupported option '--{opt}' for command '{kind.ToString().ToLowerInvariant()}'");
            }
        }

        string? configPath = options.GetSingleValue("config");
        ProfileConfig? profile = null;
        string? configDir = null;

        if (!string.IsNullOrEmpty(configPath))
        {
            var profileName = options.GetSingleValue("profile") ?? "default";
            var result = await ConfigLoader.LoadProfileAsync(configPath, profileName, cancellationToken);
            profile = result.Profile;
            configDir = result.ConfigDirectory;
            if (diagnostics != TextWriter.Null)
            {
                await diagnostics.WriteLineAsync($"Loaded profile '{profileName}' from {configPath}");
            }
        }

        // Use shell's working directory for zero-config mode
        var envDirectory = configDir ?? Directory.GetCurrentDirectory();
        var env = EnvFileLoader.LoadFromDirectory(envDirectory);
        
        var verbose = options.IsFlagSet("verbose");
        
        List<string> playlistReplaced = new();
        List<string> epgReplaced = new();
        
        var rawPlaylistUrl = options.GetSingleValue("playlist-url") ?? profile?.Inputs?.Playlist?.Url;
        var playlistSource = UrlSubstitutor.SubstituteCredentials(rawPlaylistUrl, env, out playlistReplaced);

        var rawEpgUrl = options.GetSingleValue("epg-url") ?? profile?.Inputs?.Epg?.Url;
        var epgSource = UrlSubstitutor.SubstituteCredentials(rawEpgUrl, env, out epgReplaced);

        var groupsFile = options.GetSingleValue("groups-file")
            ?? profile?.Filters?.GroupsFile
            ?? profile?.Filters?.DropListFile;

        var groupsOutputPath = options.GetSingleValue("out-groups");
        var playlistOutput = options.GetSingleValue("out-playlist") ?? profile?.Output?.PlaylistPath;
        var epgOutput = options.GetSingleValue("out-epg") ?? profile?.Output?.EpgPath;

        var liveOnly = options.IsFlagSet("live");

        if (verbose && diagnostics != TextWriter.Null)
        {
            if (env.Count > 0)
            {
                var envPath = Path.Combine(envDirectory, ".env");
                await diagnostics.WriteLineAsync($"[VERBOSE] .env file found: {envPath}");
                await diagnostics.WriteLineAsync($"[VERBOSE] Loaded {env.Count} environment variable(s): {string.Join(", ", env.Keys)}");
                
                if (playlistReplaced.Count > 0)
                {
                    await diagnostics.WriteLineAsync($"[VERBOSE] Playlist URL: substituted {string.Join(", ", playlistReplaced)}");
                }
                
                if (epgReplaced.Count > 0)
                {
                    await diagnostics.WriteLineAsync($"[VERBOSE] EPG URL: substituted {string.Join(", ", epgReplaced)}");
                }
                
                // Show the full URL structure with masked credentials
                if (!string.IsNullOrEmpty(playlistSource))
                {
                    var maskedUrl = MaskCredentialsInUrl(playlistSource);
                    await diagnostics.WriteLineAsync($"[VERBOSE] Final playlist URL: {maskedUrl}");
                }
                if (!string.IsNullOrEmpty(epgSource))
                {
                    var maskedUrl = MaskCredentialsInUrl(epgSource);
                    await diagnostics.WriteLineAsync($"[VERBOSE] Final EPG URL: {maskedUrl}");
                }
            }
            else
            {
                await diagnostics.WriteLineAsync($"[VERBOSE] No .env file found in {envDirectory}.");
            }
        }

        return new CommandContext(
            kind,
            options,
            profile,
            configPath,
            configDir,
            env,
            playlistSource,
            epgSource,
            groupsFile,
            groupsOutputPath,
            playlistOutput,
            epgOutput,
            liveOnly,
            verbose);
    }

    /// <summary>
    /// Masks sensitive credentials in a URL while preserving the structure.
    /// Replaces values for username, password, user, pass query parameters with asterisks.
    /// </summary>
    private static string MaskCredentialsInUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        // Start with the base URL (scheme + host + port + path)
        var builder = new UriBuilder(uri);
        
        if (string.IsNullOrEmpty(uri.Query))
        {
            return builder.Uri.ToString();
        }

        // Parse query string and mask sensitive parameters
        var query = uri.Query.TrimStart('?');
        var parameters = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var maskedParams = new List<string>();

        foreach (var param in parameters)
        {
            var parts = param.Split('=', 2);
            if (parts.Length != 2)
            {
                maskedParams.Add(param);
                continue;
            }

            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts[1]; // Keep URL-encoded for now

            // Check if this is a sensitive parameter
            if (IsSensitiveParameter(key))
            {
                maskedParams.Add($"{parts[0]}=***");
            }
            else
            {
                maskedParams.Add(param);
            }
        }

        builder.Query = string.Join("&", maskedParams);
        return builder.Uri.ToString();
    }

    /// <summary>
    /// Determines if a query parameter name is sensitive and should be masked.
    /// </summary>
    private static bool IsSensitiveParameter(string paramName)
    {
        var sensitive = new[] { "username", "password", "user", "pass", "pwd", "token", "apikey", "api_key", "auth" };
        return sensitive.Contains(paramName, StringComparer.OrdinalIgnoreCase);
    }
}

