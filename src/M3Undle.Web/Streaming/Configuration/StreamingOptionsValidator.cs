using Microsoft.Extensions.Options;

namespace M3Undle.Web.Streaming.Configuration;

public sealed class BufferOptionsValidator : IValidateOptions<BufferOptions>
{
    public ValidateOptionsResult Validate(string? name, BufferOptions options)
    {
        var errors = new List<string>();

        if (options.MaxBytesPerSession <= 0 || options.MaxBytesPerSession > StreamingSettingsLimits.MaxBytesPerSession)
            errors.Add($"Buffer:MaxBytesPerSession must be between 1 and {StreamingSettingsLimits.MaxBytesPerSession} bytes (1 GiB).");

        if (options.MaxBytesHardCap < options.MaxBytesPerSession)
            errors.Add("Buffer:MaxBytesHardCap must be greater than or equal to MaxBytesPerSession.");

        if (options.ReadChunkSizeBytes <= 0 || options.ReadChunkSizeBytes > StreamingSettingsLimits.MaxReadChunkSizeBytes)
            errors.Add($"Buffer:ReadChunkSizeBytes must be between 1 and {StreamingSettingsLimits.MaxReadChunkSizeBytes} bytes (16 MiB).");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class StreamProxyOptionsValidator : IValidateOptions<StreamProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, StreamProxyOptions options)
    {
        var errors = new List<string>();

        if (options.MaxConcurrentSessions <= 0)
            errors.Add("Streaming:MaxConcurrentSessions must be greater than 0.");

        if (options.IdleGrace <= TimeSpan.Zero)
            errors.Add("Streaming:IdleGrace must be greater than zero.");

        if (options.IdleGraceHardCap < options.IdleGrace)
            errors.Add("Streaming:IdleGraceHardCap must be greater than or equal to IdleGrace.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class ReconnectOptionsValidator : IValidateOptions<ReconnectOptions>
{
    public ValidateOptionsResult Validate(string? name, ReconnectOptions options)
    {
        var errors = new List<string>();

        if (options.ReadStallTimeout <= TimeSpan.Zero)
            errors.Add("Streaming:Reconnect:ReadStallTimeout must be greater than zero.");

        if (options.ContentStallTimeout <= TimeSpan.Zero)
            errors.Add("Streaming:Reconnect:ContentStallTimeout must be greater than zero.");

        if (options.OutageWindow < options.ReadStallTimeout)
            errors.Add("Streaming:Reconnect:OutageWindow must be greater than or equal to ReadStallTimeout.");

        if (options.ConnectTimeout <= TimeSpan.Zero)
            errors.Add("Streaming:Reconnect:ConnectTimeout must be greater than zero.");

        if (options.RecoveryOutputHoldLimit <= TimeSpan.Zero)
            errors.Add("Streaming:Reconnect:RecoveryOutputHoldLimit must be greater than zero.");

        if (options.RecoverySafeStartSearchLimitBytes < 188)
            errors.Add("Streaming:Reconnect:RecoverySafeStartSearchLimitBytes must be greater than or equal to one MPEG-TS packet.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class GeneratedHlsOptionsValidator : IValidateOptions<GeneratedHlsOptions>
{
    public ValidateOptionsResult Validate(string? name, GeneratedHlsOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Directory))
            errors.Add("Streaming:GeneratedHls:Directory must be set.");

        if (string.IsNullOrWhiteSpace(options.FfmpegPath))
            errors.Add("Streaming:GeneratedHls:FfmpegPath must be set.");

        if (options.SegmentDurationSeconds <= 0)
            errors.Add("Streaming:GeneratedHls:SegmentDurationSeconds must be greater than zero.");

        if (options.PlaylistSize <= 0)
            errors.Add("Streaming:GeneratedHls:PlaylistSize must be greater than zero.");

        if (options.DeleteThreshold < 1)
            errors.Add("Streaming:GeneratedHls:DeleteThreshold must be greater than or equal to 1.");

        if (options.StartupTimeoutSeconds <= 0)
            errors.Add("Streaming:GeneratedHls:StartupTimeoutSeconds must be greater than zero.");

        if (options.InactivityTimeoutSeconds <= 0)
            errors.Add("Streaming:GeneratedHls:InactivityTimeoutSeconds must be greater than zero.");

        if (options.CleanupIntervalSeconds <= 0)
            errors.Add("Streaming:GeneratedHls:CleanupIntervalSeconds must be greater than zero.");

        if (options.StartupStaleAgeHours <= 0)
            errors.Add("Streaming:GeneratedHls:StartupStaleAgeHours must be greater than zero.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
