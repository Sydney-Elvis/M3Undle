using System.Diagnostics;
using M3Undle.Web.Application;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.GeneratedHls;
using M3Undle.Web.Streaming.Observability;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Observability.Resources;

// Resource facts — measurements only, no thresholds/verdicts, except the disk-space check,
// which needs no sustained-window judgment the way CPU/memory do ("critically low" is a
// simple, static threshold). Delta-based facts (process CPU%, container CPU%, VM steal%)
// need two samples over a known interval, so this runs its own background sample loop;
// everything else is an instantaneous read, computed fresh on every GetSnapshotAsync call.
//
// Linux-only facts (container CPU/throttling, host load, VM steal) are read through
// ILinuxResourceFileReader so the parsing logic (LinuxResourceParsers) can be unit-tested
// against synthetic content independent of the host OS — every Linux-only field is simply
// null on a non-Linux host or without cgroup v2, which is the correct "omit rather than
// guess" behavior. Memory facts prefer cgroup v2 counters and fall back to the runtime's
// GC memory view when cgroup counters are unavailable.
public sealed class ResourceFactsService(
    StreamingRegistry registry,
    GeneratedHlsSessionManager generatedHlsManager,
    IOptions<GeneratedHlsOptions> generatedHlsOptions,
    RuntimePaths runtimePaths,
    ILinuxResourceFileReader linuxFileReader,
    ILogger<ResourceFactsService> logger) : IHostedService, IResourceFactsProvider
{
    private const string CgroupCpuStatPath = "/sys/fs/cgroup/cpu.stat";
    private const string CgroupCpuMaxPath = "/sys/fs/cgroup/cpu.max";
    private const string CgroupMemoryCurrentPath = "/sys/fs/cgroup/memory.current";
    private const string CgroupMemoryMaxPath = "/sys/fs/cgroup/memory.max";
    private const string CgroupMemorySwapCurrentPath = "/sys/fs/cgroup/memory.swap.current";
    private const string CgroupMemorySwapMaxPath = "/sys/fs/cgroup/memory.swap.max";
    private const string CgroupMemoryEventsPath = "/sys/fs/cgroup/memory.events";
    private const string CgroupCpuPressurePath = "/sys/fs/cgroup/cpu.pressure";
    private const string CgroupMemoryPressurePath = "/sys/fs/cgroup/memory.pressure";
    private const string CgroupIoPressurePath = "/sys/fs/cgroup/io.pressure";
    private const string ProcStatPath = "/proc/stat";
    private const string ProcLoadAvgPath = "/proc/loadavg";
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

    // A volume is "critically low" below 5% free or 1 GiB free, whichever is more
    // conservative — a simple, static threshold; disk space doesn't need the sustained-window
    // caution CPU/memory readings do.
    private const double DiskCriticalFreeFraction = 0.05;
    private const long DiskCriticalFreeBytes = 1_073_741_824;

    private readonly CancellationTokenSource _cts = new();
    private Task? _sampleTask;
    private int _stopped;

    // Delta-sampling state — only ever written by the single periodic loop, no concurrency
    // concern. The computed rates are read from arbitrary request threads via
    // GetSnapshotAsync, so they're published as one small immutable record swapped
    // atomically via Volatile.Read/Write.
    private sealed record SampledRates(
        double? ProcessCpuPercent,
        double? ContainerCpuPercent,
        double? ContainerCpuThrottledPercent,
        double? VmCpuStealPercent)
    {
        public static readonly SampledRates Empty = new(null, null, null, null);
    }

    private DateTimeOffset _lastSampleUtc = DateTimeOffset.UtcNow;
    private TimeSpan _lastProcessCpuTime = GetCurrentProcessCpuTime();
    private long? _lastCgroupUsageUsec;
    private long? _lastCgroupPeriods;
    private long? _lastCgroupThrottledPeriods;
    private long? _lastProcStatTotal;
    private long? _lastProcStatSteal;

    private SampledRates _rates = SampledRates.Empty;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sampleTask = RunSampleLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        await _cts.CancelAsync();
        if (_sampleTask is not null)
            await _sampleTask;
        _cts.Dispose();
    }

    private static TimeSpan GetCurrentProcessCpuTime()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    private async Task RunSampleLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SampleInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                Sample();
        }
        catch (OperationCanceledException) { }
    }

    // internal (not private) so tests can drive a sample tick directly without the
    // IHostedService/PeriodicTimer machinery.
    internal void Sample()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsedSeconds = (now - _lastSampleUtc).TotalSeconds;
        if (elapsedSeconds < 1.0)
            return;

        var processorCount = Math.Max(1, Environment.ProcessorCount);
        var availableCpuCores = ParseCpuLimit()?.Cores ?? processorCount;

        var currentCpuTime = GetCurrentProcessCpuTime();
        var cpuDeltaSeconds = (currentCpuTime - _lastProcessCpuTime).TotalSeconds;
        var processCpuPercent = Math.Clamp(cpuDeltaSeconds / elapsedSeconds / availableCpuCores * 100.0, 0, 100);
        _lastProcessCpuTime = currentCpuTime;

        double? containerCpuPercent = null;
        double? containerCpuThrottledPercent = null;
        var cpuStatContent = linuxFileReader.TryReadAllText(CgroupCpuStatPath);
        if (cpuStatContent is not null && LinuxResourceParsers.ParseCgroupCpuStat(cpuStatContent) is { } cpuStat)
        {
            if (_lastCgroupUsageUsec is { } lastUsage)
            {
                var usageDeltaSeconds = (cpuStat.UsageUsec - lastUsage) / 1_000_000.0;
                containerCpuPercent = Math.Clamp(usageDeltaSeconds / elapsedSeconds / availableCpuCores * 100.0, 0, 100);
            }
            if (_lastCgroupPeriods is { } lastPeriods && _lastCgroupThrottledPeriods is { } lastThrottled)
            {
                var periodDelta = cpuStat.NrPeriods - lastPeriods;
                var throttledDelta = cpuStat.NrThrottled - lastThrottled;
                containerCpuThrottledPercent = periodDelta > 0
                    ? Math.Clamp((double)throttledDelta / periodDelta * 100.0, 0, 100)
                    : 0;
            }
            _lastCgroupUsageUsec = cpuStat.UsageUsec;
            _lastCgroupPeriods = cpuStat.NrPeriods;
            _lastCgroupThrottledPeriods = cpuStat.NrThrottled;
        }
        else
        {
            _lastCgroupUsageUsec = null;
            _lastCgroupPeriods = null;
            _lastCgroupThrottledPeriods = null;
        }

        double? vmCpuStealPercent = null;
        var procStatContent = linuxFileReader.TryReadAllText(ProcStatPath);
        if (procStatContent is not null && LinuxResourceParsers.ParseProcStatAggregateCpuLine(procStatContent) is { } procStat)
        {
            if (_lastProcStatTotal is { } lastTotal)
            {
                var totalDelta = procStat.Total - lastTotal;
                var stealDelta = procStat.Steal - (_lastProcStatSteal ?? procStat.Steal);
                vmCpuStealPercent = totalDelta > 0 ? Math.Clamp((double)stealDelta / totalDelta * 100.0, 0, 100) : 0.0;
            }
            _lastProcStatTotal = procStat.Total;
            _lastProcStatSteal = procStat.Steal;
        }
        else
        {
            _lastProcStatTotal = null;
            _lastProcStatSteal = null;
        }

        Volatile.Write(ref _rates, new SampledRates(
            processCpuPercent,
            containerCpuPercent,
            containerCpuThrottledPercent,
            vmCpuStealPercent));
        _lastSampleUtc = now;
    }

    public Task<ResourceFacts> GetSnapshotAsync(CancellationToken ct = default)
    {
        long processWorkingSetBytes;
        using (var process = Process.GetCurrentProcess())
            processWorkingSetBytes = process.WorkingSet64;
        var gcInfo = GC.GetGCMemoryInfo();
        var memoryCurrent = ParseByteFile(CgroupMemoryCurrentPath);
        var memoryMaxContent = linuxFileReader.TryReadAllText(CgroupMemoryMaxPath);
        var memoryMax = memoryMaxContent is null ? null : LinuxResourceParsers.ParseCgroupByteValue(memoryMaxContent);
        var cpuLimit = ParseCpuLimit();
        var hasCgroupMemory = memoryCurrent is not null;
        var hasCgroupMemoryLimit = memoryMax is > 0;
        var memoryEvents = ParseMemoryEventsFile();

        long? containerThrottledPeriods = null;
        TimeSpan? containerThrottledTime = null;
        var cpuStatContent = linuxFileReader.TryReadAllText(CgroupCpuStatPath);
        if (cpuStatContent is not null && LinuxResourceParsers.ParseCgroupCpuStat(cpuStatContent) is { } cpuStat)
        {
            containerThrottledPeriods = cpuStat.NrThrottled;
            containerThrottledTime = TimeSpan.FromTicks(cpuStat.ThrottledUsec * 10); // 1us = 10 ticks
        }

        LinuxResourceParsers.LoadAverage? hostLoadAverage = null;
        var loadAvgContent = linuxFileReader.TryReadAllText(ProcLoadAvgPath);
        if (loadAvgContent is not null)
            hostLoadAverage = LinuxResourceParsers.ParseLoadAverage(loadAvgContent);

        var clients = registry.GetActiveClients();
        var aggregateEgress = clients.Sum(c => c.BytesPerSecond ?? 0);
        var rates = Volatile.Read(ref _rates);

        var facts = new ResourceFacts(
            SampledUtc: DateTimeOffset.UtcNow,
            ProcessCpuPercent: rates.ProcessCpuPercent,
            ProcessWorkingSetBytes: processWorkingSetBytes,
            ContainerCpuPercent: rates.ContainerCpuPercent,
            RuntimeProcessorCount: Math.Max(1, Environment.ProcessorCount),
            ContainerCpuLimitCores: cpuLimit?.Cores,
            ContainerCpuLimitFileAvailable: cpuLimit is not null,
            ContainerCpuThrottledPercent: rates.ContainerCpuThrottledPercent,
            ContainerCpuThrottledPeriods: containerThrottledPeriods,
            ContainerCpuThrottledTime: containerThrottledTime,
            ContainerMemoryUsedBytes: hasCgroupMemory ? memoryCurrent!.Value : gcInfo.MemoryLoadBytes,
            ContainerMemoryLimitBytes: hasCgroupMemory ? memoryMax ?? 0 : gcInfo.TotalAvailableMemoryBytes,
            RuntimeMemoryAvailableBytes: gcInfo.TotalAvailableMemoryBytes,
            ContainerMemoryIsCgroupMeasurement: hasCgroupMemory,
            ContainerMemoryHasExplicitLimit: hasCgroupMemoryLimit,
            ContainerMemoryLimitFileAvailable: memoryMaxContent is not null,
            ContainerSwapUsedBytes: ParseByteFile(CgroupMemorySwapCurrentPath),
            ContainerSwapLimitBytes: ParseByteFile(CgroupMemorySwapMaxPath),
            ContainerMemoryHighEventCount: memoryEvents?.High,
            ContainerMemoryMaxEventCount: memoryEvents?.Max,
            ContainerOomEventCount: memoryEvents?.Oom,
            ContainerOomKillCount: memoryEvents?.OomKill,
            ContainerCpuPressurePercent: ParsePressureFile(CgroupCpuPressurePath),
            ContainerMemoryPressurePercent: ParsePressureFile(CgroupMemoryPressurePath),
            ContainerIoPressurePercent: ParsePressureFile(CgroupIoPressurePath),
            HostLoadAverage1Min: hostLoadAverage?.OneMinute,
            HostLoadAverage5Min: hostLoadAverage?.FiveMinutes,
            HostLoadAverage15Min: hostLoadAverage?.FifteenMinutes,
            VmCpuStealPercent: rates.VmCpuStealPercent,
            ActiveFfmpegProcessCount: generatedHlsManager.ActiveSessionCount,
            AggregateEgressBytesPerSecond: aggregateEgress,
            ActiveClientCount: clients.Count,
            DiskVolumes: ComputeDiskVolumes());

        return Task.FromResult(facts);
    }

    private LinuxResourceParsers.CpuLimit? ParseCpuLimit()
    {
        var content = linuxFileReader.TryReadAllText(CgroupCpuMaxPath);
        return content is null ? null : LinuxResourceParsers.ParseCgroupCpuMax(content);
    }

    private long? ParseByteFile(string path)
    {
        var content = linuxFileReader.TryReadAllText(path);
        return content is null ? null : LinuxResourceParsers.ParseCgroupByteValue(content);
    }

    private LinuxResourceParsers.MemoryEvents? ParseMemoryEventsFile()
    {
        var content = linuxFileReader.TryReadAllText(CgroupMemoryEventsPath);
        return content is null ? null : LinuxResourceParsers.ParseMemoryEvents(content);
    }

    private double? ParsePressureFile(string path)
    {
        var content = linuxFileReader.TryReadAllText(path);
        return content is null ? null : LinuxResourceParsers.ParsePressureSomeAverage10(content);
    }

    private List<DiskVolumeFact> ComputeDiskVolumes()
    {
        var paths = new (string Label, string Path)[]
        {
            ("Database", runtimePaths.DataDirectory),
            ("Logs", runtimePaths.LogDirectory),
            ("Generated HLS", generatedHlsOptions.Value.Directory),
        };

        var results = new List<DiskVolumeFact>();
        foreach (var (label, path) in paths)
        {
            try
            {
                // MountResolver, not Path.GetPathRoot: the latter returns "/" for every Unix
                // path, which measured the container's overlay filesystem instead of the volume
                // the files actually live on.
                var drive = MountResolver.ForPath(path);
                if (drive is null)
                    continue;
                var isCritical = drive.TotalSize > 0
                    && (drive.AvailableFreeSpace < DiskCriticalFreeBytes
                        || (double)drive.AvailableFreeSpace / drive.TotalSize < DiskCriticalFreeFraction);

                results.Add(new DiskVolumeFact(
                    Label: label,
                    Path: path,
                    FreeBytes: drive.AvailableFreeSpace,
                    TotalBytes: drive.TotalSize,
                    IsCritical: isCritical));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogDebug(ex, "Could not read free disk space for {Label} ({Path}).", label, path);
            }
        }

        return results;
    }
}
