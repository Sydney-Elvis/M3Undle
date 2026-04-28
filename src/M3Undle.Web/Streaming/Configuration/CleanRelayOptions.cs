using Microsoft.Extensions.Options;

namespace M3Undle.Web.Streaming.Configuration;

public static class CleanRelayModes
{
    public const string Off = "off";
    public const string Remux = "remux";

    public static string Normalize(string? value)
        => string.Equals(value, Remux, StringComparison.OrdinalIgnoreCase) ? Remux : Off;

    public static bool IsRemux(string? value)
        => string.Equals(value, Remux, StringComparison.OrdinalIgnoreCase);
}

public sealed class CleanRelayOptions
{
    public string? FfmpegPath { get; set; }

    public int StartupTimeoutSeconds { get; set; } = 10;

    public bool FallbackToDirect { get; set; } = true;

    public int MaxStartupBytes { get; set; } = 8192;
}

public sealed class CleanRelayOptionsValidator : IValidateOptions<CleanRelayOptions>
{
    public ValidateOptionsResult Validate(string? name, CleanRelayOptions options)
    {
        var errors = new List<string>();

        if (options.StartupTimeoutSeconds <= 0)
            errors.Add("Streaming:CleanRelay:StartupTimeoutSeconds must be greater than zero.");

        if (options.MaxStartupBytes < 188)
            errors.Add("Streaming:CleanRelay:MaxStartupBytes must be at least 188 bytes.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
