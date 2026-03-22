namespace M3Undle.Web.Streaming.Configuration;

public sealed class BufferOptions
{
    public int MaxBytesPerSession { get; set; } = 4 * 1024 * 1024;

    public int MaxBytesHardCap { get; set; } = 32 * 1024 * 1024;

    public int ReadChunkSizeBytes { get; set; } = 32 * 1024;

    public int SubscriberQueueCapacity { get; set; } = 128;
}

