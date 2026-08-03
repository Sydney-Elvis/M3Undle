namespace M3Undle.Web.Observability.Resources;

// Measurements only — no thresholds, no verdicts. Every nullable field is omitted (null)
// rather than guessed when the read source isn't available on this platform/deployment
// (non-Linux host, no cgroup v2, not virtualized).
public sealed record ResourceFacts(
    DateTimeOffset SampledUtc,

    // The M3Undle .NET process itself.
    double? ProcessCpuPercent,
    long ProcessWorkingSetBytes,

    // The M3Undle container as a whole, including FFmpeg child processes. CPU fields are
    // Linux cgroup v2 only — null everywhere else (no cgroup v2, non-Linux host). Memory
    // fields prefer direct cgroup measurements and fall back to GC.GetGCMemoryInfo() so,
    // unlike the CPU facts, a useful memory view remains available cross-platform.
    double? ContainerCpuPercent,
    int RuntimeProcessorCount,
    double? ContainerCpuLimitCores,
    bool ContainerCpuLimitFileAvailable,
    double? ContainerCpuThrottledPercent,
    long? ContainerCpuThrottledPeriods,
    TimeSpan? ContainerCpuThrottledTime,
    long ContainerMemoryUsedBytes,
    long ContainerMemoryLimitBytes,
    long RuntimeMemoryAvailableBytes,
    bool ContainerMemoryIsCgroupMeasurement,
    bool ContainerMemoryHasExplicitLimit,
    bool ContainerMemoryLimitFileAvailable,
    long? ContainerSwapUsedBytes,
    long? ContainerSwapLimitBytes,
    long? ContainerMemoryHighEventCount,
    long? ContainerMemoryMaxEventCount,
    long? ContainerOomEventCount,
    long? ContainerOomKillCount,
    double? ContainerCpuPressurePercent,
    double? ContainerMemoryPressurePercent,
    double? ContainerIoPressurePercent,

    // Host capacity visible to M3Undle. Two different signals for "something else on this
    // host is competing with M3Undle": steal time only means something when virtualized;
    // host load average applies everywhere, virtualized or not.
    double? HostLoadAverage1Min,
    double? HostLoadAverage5Min,
    double? HostLoadAverage15Min,
    double? VmCpuStealPercent,

    // M3Undle child processes (FFmpeg).
    int ActiveFfmpegProcessCount,

    // Cross-platform, not tied to any one boundary.
    long AggregateEgressBytesPerSecond,
    int ActiveClientCount,
    IReadOnlyList<DiskVolumeFact> DiskVolumes);

public sealed record DiskVolumeFact(
    string Label,
    string Path,
    long FreeBytes,
    long TotalBytes,
    bool IsCritical);
