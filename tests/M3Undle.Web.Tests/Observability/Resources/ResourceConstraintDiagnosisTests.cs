using M3Undle.Web.Observability.Resources;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Observability.Resources;

[TestClass]
public sealed class ResourceConstraintDiagnosisTests
{
    [TestMethod]
    public void Evaluate_QuotaThrottling_AttributesConstraintToDockerLimit()
    {
        var findings = ResourceConstraintDiagnosis.Evaluate(CreateFacts(
            cpuLimit: 0.5,
            cpuThrottledPercent: 78));

        Assert.IsTrue(findings.Any(finding =>
            finding.Message.Contains("Docker CPU limit is constraining", StringComparison.Ordinal)
            && finding.Message.Contains("0.50-CPU quota", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Evaluate_CpuPressureWithoutQuota_UsesQualifiedHostAttribution()
    {
        var findings = ResourceConstraintDiagnosis.Evaluate(CreateFacts(cpuPressurePercent: 17.5));

        Assert.IsTrue(findings.Any(finding =>
            finding.Message.Contains("host/VM CPU", StringComparison.Ordinal)
            && finding.Message.Contains("likely", StringComparison.Ordinal)));
        Assert.IsFalse(findings.Any(finding => finding.Message.Contains("Docker CPU limit is constraining", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Evaluate_LoadAverageAlone_DoesNotClaimAConstraint()
    {
        var findings = ResourceConstraintDiagnosis.Evaluate(CreateFacts(
            loadOne: 32,
            loadFive: 24,
            loadFifteen: 16));

        Assert.AreEqual(1, findings.Count);
        Assert.AreEqual(ResourceConstraintSeverity.Information, findings[0].Severity);
        StringAssert.Contains(findings[0].Message, "No current");
    }

    [TestMethod]
    public void Evaluate_MemoryLimitEvent_AttributesConstraintToDockerLimit()
    {
        var findings = ResourceConstraintDiagnosis.Evaluate(CreateFacts(
            memoryLimit: 768 * 1024 * 1024,
            memoryMaxEvents: 3));

        Assert.IsTrue(findings.Any(finding =>
            finding.Severity == ResourceConstraintSeverity.Critical
            && finding.Message.Contains("Docker memory limit has been reached", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void FooterCpuPresentation_UsesDiagnosisSeverityAndExplanation()
    {
        var facts = CreateFacts(cpuLimit: 0.5, cpuThrottledPercent: 78);

        Assert.AreEqual(ResourceConstraintSeverity.Critical, ResourceFactsPresentation.GetFooterCpuSeverity(facts));
        StringAssert.Contains(ResourceFactsPresentation.BuildFooterSummarySentence(facts), "Docker CPU limit is constraining");
    }

    [TestMethod]
    public void FooterCpuPresentation_HighUsageWithoutConstraint_IsWarning()
    {
        var facts = CreateFacts(containerCpuPercent: 92);

        Assert.AreEqual(ResourceConstraintSeverity.Warning, ResourceFactsPresentation.GetFooterCpuSeverity(facts));
        StringAssert.Contains(ResourceFactsPresentation.BuildFooterSummarySentence(facts), "sample alone does not prove host contention");
    }

    private static ResourceFacts CreateFacts(
        double? cpuLimit = null,
        double? containerCpuPercent = 0,
        double? cpuThrottledPercent = null,
        double? cpuPressurePercent = null,
        long memoryLimit = 0,
        long? memoryMaxEvents = 0,
        double? loadOne = null,
        double? loadFive = null,
        double? loadFifteen = null) => new(
            SampledUtc: DateTimeOffset.UtcNow,
            ProcessCpuPercent: 0,
            ProcessWorkingSetBytes: 100,
            ContainerCpuPercent: containerCpuPercent,
            RuntimeProcessorCount: 1,
            ContainerCpuLimitCores: cpuLimit,
            ContainerCpuLimitFileAvailable: true,
            ContainerCpuThrottledPercent: cpuThrottledPercent,
            ContainerCpuThrottledPeriods: 0,
            ContainerCpuThrottledTime: TimeSpan.Zero,
            ContainerMemoryUsedBytes: 100,
            ContainerMemoryLimitBytes: memoryLimit,
            RuntimeMemoryAvailableBytes: memoryLimit,
            ContainerMemoryIsCgroupMeasurement: true,
            ContainerMemoryHasExplicitLimit: memoryLimit > 0,
            ContainerMemoryLimitFileAvailable: true,
            ContainerSwapUsedBytes: 0,
            ContainerSwapLimitBytes: 0,
            ContainerMemoryHighEventCount: 0,
            ContainerMemoryMaxEventCount: memoryMaxEvents,
            ContainerOomEventCount: 0,
            ContainerOomKillCount: 0,
            ContainerCpuPressurePercent: cpuPressurePercent,
            ContainerMemoryPressurePercent: 0,
            ContainerIoPressurePercent: 0,
            HostLoadAverage1Min: loadOne,
            HostLoadAverage5Min: loadFive,
            HostLoadAverage15Min: loadFifteen,
            VmCpuStealPercent: 0,
            ActiveFfmpegProcessCount: 0,
            AggregateEgressBytesPerSecond: 0,
            ActiveClientCount: 0,
            DiskVolumes: []);
}
