using M3Undle.Web.Application;
using M3Undle.Web.Observability.Resources;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.GeneratedHls;
using M3Undle.Web.Streaming.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Observability.Resources;

[TestClass]
public sealed class ResourceFactsServiceTests
{
    [TestMethod]
    public async Task GetSnapshotAsync_BeforeAnySample_LeavesDeltaBasedCpuFactsNull()
    {
        var service = CreateService(new FakeLinuxResourceFileReader());

        var facts = await service.GetSnapshotAsync();

        // Delta-based facts need two samples over a known interval — before Sample() has ever
        // run, there's nothing to compute a rate from, so these must be omitted rather than
        // guessed at (e.g. reported as 0%).
        Assert.IsNull(facts.ProcessCpuPercent);
        Assert.IsNull(facts.ContainerCpuPercent);
        Assert.IsNull(facts.VmCpuStealPercent);
    }

    [TestMethod]
    public async Task GetSnapshotAsync_NoCgroupOrProcFiles_LeavesLinuxOnlyFactsNull()
    {
        var reader = new FakeLinuxResourceFileReader();
        var service = CreateService(reader);

        service.Sample();
        var facts = await service.GetSnapshotAsync();

        Assert.IsNull(facts.ContainerCpuPercent);
        Assert.IsNull(facts.VmCpuStealPercent);
        Assert.IsNull(facts.HostLoadAverage1Min);
        Assert.IsNull(facts.ContainerCpuThrottledPeriods);
        Assert.IsNull(facts.ContainerCpuThrottledTime);
    }

    [TestMethod]
    public async Task Sample_TwoTicksWithGrowingCgroupUsage_ComputesContainerCpuPercent()
    {
        var reader = new FakeLinuxResourceFileReader
        {
            CgroupCpuStat = "usage_usec 1000000\nnr_periods 1\nnr_throttled 0\nthrottled_usec 0\n",
        };
        var service = CreateService(reader);

        // Sample() no-ops until >= 1s has elapsed since construction/the last successful
        // sample, so the very first tick right after construction must be given time to
        // clear that guard before it can establish the baseline usage figure.
        await Task.Delay(1100);
        service.Sample();

        // Simulate ~1 full core-second of container CPU usage over ~1 wall-clock second.
        reader.CgroupCpuStat = "usage_usec 2000000\nnr_periods 2\nnr_throttled 0\nthrottled_usec 0\n";
        await Task.Delay(1100);
        service.Sample();

        var facts = await service.GetSnapshotAsync();

        Assert.IsNotNull(facts.ContainerCpuPercent);
        Assert.IsTrue(facts.ContainerCpuPercent > 0, $"Expected positive container CPU%, got {facts.ContainerCpuPercent}");
    }

    [TestMethod]
    public async Task GetSnapshotAsync_CgroupThrottlingPresent_ReportsThrottledPeriodsAndTime()
    {
        var reader = new FakeLinuxResourceFileReader
        {
            CgroupCpuStat = "usage_usec 5000000\nnr_periods 100\nnr_throttled 7\nthrottled_usec 250000\n",
        };
        var service = CreateService(reader);

        var facts = await service.GetSnapshotAsync();

        Assert.AreEqual(7L, facts.ContainerCpuThrottledPeriods);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), facts.ContainerCpuThrottledTime);
    }

    [TestMethod]
    public async Task GetSnapshotAsync_LoadAverageFilePresent_ReportsHostLoadAverage()
    {
        var reader = new FakeLinuxResourceFileReader { LoadAverage = "1.25 1.10 0.95 2/345 6789\n" };
        var service = CreateService(reader);

        var facts = await service.GetSnapshotAsync();

        Assert.AreEqual(1.25, facts.HostLoadAverage1Min);
    }

    [TestMethod]
    public async Task GetSnapshotAsync_MemoryFacts_AreAlwaysReported()
    {
        // Unlike the Linux-only CPU facts, GC.GetGCMemoryInfo() works cross-platform (and is
        // cgroup-aware on Linux), so these must never be null/omitted the way container CPU%
        // is on a non-Linux dev machine.
        var service = CreateService(new FakeLinuxResourceFileReader());

        var facts = await service.GetSnapshotAsync();

        Assert.IsTrue(facts.ContainerMemoryLimitBytes > 0, "Expected a positive memory limit from GC.GetGCMemoryInfo().");
        Assert.IsTrue(facts.ProcessWorkingSetBytes > 0, "Expected a positive process working-set size.");
    }

    [TestMethod]
    public async Task GetSnapshotAsync_NoActiveClientsOrFfmpegSessions_ReportsZeroCounts()
    {
        var service = CreateService(new FakeLinuxResourceFileReader());

        var facts = await service.GetSnapshotAsync();

        Assert.AreEqual(0, facts.ActiveClientCount);
        Assert.AreEqual(0, facts.ActiveFfmpegProcessCount);
        Assert.AreEqual(0L, facts.AggregateEgressBytesPerSecond);
    }

    [TestMethod]
    public async Task GetSnapshotAsync_ReportsDiskVolumeForEachConfiguredPath()
    {
        var service = CreateService(new FakeLinuxResourceFileReader());

        var facts = await service.GetSnapshotAsync();

        Assert.AreEqual(2, facts.DiskVolumes.Count);
        CollectionAssert.AreEquivalent(
            new[] { "Logs", "Generated HLS" },
            facts.DiskVolumes.Select(v => v.Label).ToArray());
    }

    private static ResourceFactsService CreateService(ILinuxResourceFileReader linuxFileReader)
    {
        var tempDir = Directory.CreateTempSubdirectory("resource-facts-tests-").FullName;
        var registry = new StreamingRegistry(Options.Create(new StreamProxyOptions()));
        var generatedHlsOptions = Options.Create(new GeneratedHlsOptions
        {
            Directory = Path.Combine(tempDir, "generated-hls"),
        });
        var generatedHlsManager = new GeneratedHlsSessionManager(
            generatedHlsOptions,
            scopeFactory: null!,
            channelSessionManager: null!,
            registry,
            NullLogger<GeneratedHlsSessionManager>.Instance);
        var runtimePaths = new RuntimePaths(
            DataDirectory: tempDir,
            DatabasePath: Path.Combine(tempDir, "test.db"),
            DatabaseConnectionString: $"Data Source={Path.Combine(tempDir, "test.db")}",
            LogDirectory: tempDir,
            SnapshotDirectory: tempDir);

        return new ResourceFactsService(
            registry,
            generatedHlsManager,
            generatedHlsOptions,
            runtimePaths,
            linuxFileReader,
            NullLogger<ResourceFactsService>.Instance);
    }

    private sealed class FakeLinuxResourceFileReader : ILinuxResourceFileReader
    {
        public string? CgroupCpuStat { get; set; }
        public string? ProcStat { get; set; }
        public string? LoadAverage { get; set; }

        public string? TryReadAllText(string path) => path switch
        {
            "/sys/fs/cgroup/cpu.stat" => CgroupCpuStat,
            "/proc/stat" => ProcStat,
            "/proc/loadavg" => LoadAverage,
            _ => null,
        };
    }
}
