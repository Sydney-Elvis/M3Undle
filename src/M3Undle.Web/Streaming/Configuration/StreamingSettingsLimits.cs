namespace M3Undle.Web.Streaming.Configuration;

/// <summary>Hard upper bounds for streaming settings accepted via UI or config.</summary>
internal static class StreamingSettingsLimits
{
    /// <summary>Maximum buffer per session: 1 GiB.</summary>
    public const int MaxBytesPerSession = 1 * 1024 * 1024 * 1024;

    /// <summary>Maximum read chunk size: 16 MiB.</summary>
    public const int MaxReadChunkSizeBytes = 16 * 1024 * 1024;
}
