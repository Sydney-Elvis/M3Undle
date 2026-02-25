using System.Collections.Frozen;
using Microsoft.Extensions.Logging;

namespace M3Undle.Web.Application;

/// <summary>
/// Loads and manages environment variables from .env files in M3UNDLE_CONFIG_DIR.
/// Provides substitution of %VAR% placeholders in provider URLs.
/// </summary>
public sealed class EnvironmentVariableService
{
    private readonly FrozenDictionary<string, string> _variables;
    private readonly ILogger<EnvironmentVariableService> _logger;

    public EnvironmentVariableService(ILogger<EnvironmentVariableService> logger)
    {
        _logger = logger;
        _variables = LoadEnvFile();
    }

    /// <summary>
    /// Substitutes all %VAR% placeholders in the input string with values from .env.
    /// Throws if a referenced variable is not defined.
    /// </summary>
    public string SubstituteEnvVars(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("%"))
            return input;

        var result = input;
        var matches = System.Text.RegularExpressions.Regex.Matches(input, @"%(\w+)%");

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var varName = match.Groups[1].Value;
            if (!_variables.TryGetValue(varName, out var value))
            {
                throw new InvalidOperationException(
                    $"Environment variable '{varName}' referenced in URL but not defined in .env");
            }

            result = result.Replace($"%{varName}%", value);
        }

        return result;
    }

    /// <summary>
    /// Checks if a URL template contains any %VAR% placeholders.
    /// </summary>
    public bool RequiresSubstitution(string? input)
    {
        return !string.IsNullOrEmpty(input) && input.Contains("%");
    }

    /// <summary>
    /// Gets all variable names referenced in the input string.
    /// </summary>
    public IEnumerable<string> GetReferencedVariables(string? input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("%"))
            yield break;

        var matches = System.Text.RegularExpressions.Regex.Matches(input, @"%(\w+)%");
        var seen = new HashSet<string>();

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var varName = match.Groups[1].Value;
            if (seen.Add(varName))
                yield return varName;
        }
    }

    /// <summary>
    /// Validates that all variables referenced in the input are defined in .env.
    /// Returns (isValid, missingVariables).
    /// </summary>
    public (bool IsValid, IReadOnlyList<string> Missing) ValidateVariables(string? input)
    {
        var missing = GetReferencedVariables(input)
            .Where(v => !_variables.ContainsKey(v))
            .ToList();

        return (missing.Count == 0, missing.AsReadOnly());
    }

    /// <summary>
    /// Gets the underlying environment variables dictionary (for diagnostics/admin views).
    /// Only returns variable names, not values (for security).
    /// </summary>
    public IEnumerable<string> GetDefinedVariableNames() => _variables.Keys;

    private FrozenDictionary<string, string> LoadEnvFile()
    {
        var configDir = GetConfigDir();
        if (string.IsNullOrEmpty(configDir))
        {
            _logger.LogWarning("M3UNDLE_CONFIG_DIR not set; no .env file will be loaded");
            return FrozenDictionary<string, string>.Empty;
        }

        var envPath = Path.Combine(configDir, ".env");
        if (!File.Exists(envPath))
        {
            _logger.LogWarning("No .env file found at {EnvPath}; environment substitution unavailable", envPath);
            return FrozenDictionary<string, string>.Empty;
        }

        var vars = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            foreach (var line in File.ReadLines(envPath))
            {
                var trimmed = line.Trim();

                // Skip empty lines and comments
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                var eqIndex = trimmed.IndexOf('=');
                if (eqIndex <= 0)
                    continue;

                var key = trimmed[..eqIndex].Trim();
                var value = trimmed[(eqIndex + 1)..].Trim();

                // Remove surrounding quotes if present
                if ((value.StartsWith('"') && value.EndsWith('"')) ||
                    (value.StartsWith('\'') && value.EndsWith('\'')))
                {
                    value = value[1..^1];
                }

                if (!string.IsNullOrEmpty(key))
                {
                    vars[key] = value;
                }
            }

            _logger.LogInformation("Loaded {VarCount} environment variables from {EnvPath}", vars.Count, envPath);
            return vars.ToFrozenDictionary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load .env file from {EnvPath}", envPath);
            return FrozenDictionary<string, string>.Empty;
        }
    }

    private static string? GetConfigDir()
    {
        return Environment.GetEnvironmentVariable("M3UNDLE_CONFIG_DIR");
    }

}

