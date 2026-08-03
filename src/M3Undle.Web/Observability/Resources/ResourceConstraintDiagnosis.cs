namespace M3Undle.Web.Observability.Resources;

public enum ResourceConstraintSeverity
{
    Information,
    Warning,
    Critical,
}

public sealed record ResourceConstraintFinding(ResourceConstraintSeverity Severity, string Message);

// Maps measurements to conclusions only where the source supports the attribution. In
// particular, cgroup throttling/limit events can implicate container configuration, while
// PSI proves that this cgroup waited but cannot identify the competing host workload.
public static class ResourceConstraintDiagnosis
{
    public static IReadOnlyList<ResourceConstraintFinding> Evaluate(ResourceFacts facts)
    {
        var findings = new List<ResourceConstraintFinding>();

        AddStorageFindings(facts, findings);
        AddMemoryFindings(facts, findings);
        AddCpuFindings(facts, findings);
        AddIoFindings(facts, findings);

        if (findings.Count == 0)
        {
            findings.Add(new(
                ResourceConstraintSeverity.Information,
                "No current CPU, memory, or storage constraint is visible to M3Undle."));
        }

        return findings;
    }

    public static IReadOnlyList<ResourceConstraintFinding> EvaluateCpu(ResourceFacts facts)
    {
        var findings = new List<ResourceConstraintFinding>();
        AddCpuFindings(facts, findings);
        return findings;
    }

    private static void AddCpuFindings(ResourceFacts facts, List<ResourceConstraintFinding> findings)
    {
        var throttled = facts.ContainerCpuThrottledPercent ?? 0;
        var pressure = facts.ContainerCpuPressurePercent ?? 0;
        var cpu = facts.ContainerCpuPercent ?? 0;

        if (facts.ContainerCpuLimitCores is { } limit && throttled >= 5)
        {
            findings.Add(new(
                throttled >= 50 ? ResourceConstraintSeverity.Critical : ResourceConstraintSeverity.Warning,
                $"The Docker CPU limit is constraining M3Undle: {throttled:F1}% of recent scheduling periods were throttled against its {limit:F2}-CPU quota."));
        }
        else if (pressure >= 10)
        {
            var scope = facts.ContainerCpuLimitCores is null
                ? "No Docker CPU quota was detected, so insufficient host/VM CPU or competition from other workloads is likely."
                : "Quota throttling is not the primary signal, so host/VM CPU contention may also be involved.";
            findings.Add(new(
                ResourceConstraintSeverity.Warning,
                $"M3Undle tasks are waiting for CPU time ({pressure:F1}% recent CPU pressure). {scope}"));
        }
        else if (cpu >= 90)
        {
            findings.Add(new(
                ResourceConstraintSeverity.Warning,
                $"M3Undle is using nearly all CPU currently available to it ({cpu:F1}%). More CPU may help this workload, but this sample alone does not prove host contention."));
        }

        if (facts.VmCpuStealPercent >= 5)
        {
            findings.Add(new(
                ResourceConstraintSeverity.Warning,
                $"The hypervisor withheld {facts.VmCpuStealPercent:F1}% of recent CPU time, indicating contention outside this VM."));
        }
    }

    private static void AddMemoryFindings(ResourceFacts facts, List<ResourceConstraintFinding> findings)
    {
        var usedPercent = facts.ContainerMemoryLimitBytes > 0
            ? facts.ContainerMemoryUsedBytes * 100.0 / facts.ContainerMemoryLimitBytes
            : 0;

        if (facts.ContainerOomKillCount > 0)
        {
            findings.Add(new(
                ResourceConstraintSeverity.Critical,
                $"The container has recorded {facts.ContainerOomKillCount:N0} out-of-memory kill(s) since start. Its memory allocation is insufficient for at least one observed workload."));
        }
        else if (facts.ContainerMemoryHasExplicitLimit && facts.ContainerMemoryMaxEventCount > 0)
        {
            findings.Add(new(
                ResourceConstraintSeverity.Critical,
                $"The Docker memory limit has been reached {facts.ContainerMemoryMaxEventCount:N0} time(s) since start. Increase the limit or reduce the workload."));
        }
        else if (facts.ContainerMemoryHasExplicitLimit && usedPercent >= 90)
        {
            findings.Add(new(
                usedPercent >= 95 ? ResourceConstraintSeverity.Critical : ResourceConstraintSeverity.Warning,
                $"Container memory is {usedPercent:F0}% used and is close to its Docker limit."));
        }

        if (facts.ContainerMemoryPressurePercent >= 2)
        {
            var attribution = facts.ContainerMemoryHasExplicitLimit
                ? "The Docker limit or broader host memory contention can cause this; no limit event currently identifies one conclusively."
                : "No Docker memory limit was detected, so host/VM memory contention is likely, but M3Undle cannot identify the competing workload.";
            findings.Add(new(
                facts.ContainerMemoryPressurePercent >= 10 ? ResourceConstraintSeverity.Critical : ResourceConstraintSeverity.Warning,
                $"M3Undle tasks are stalling for memory ({facts.ContainerMemoryPressurePercent:F1}% recent memory pressure). {attribution}"));
        }
    }

    private static void AddStorageFindings(ResourceFacts facts, List<ResourceConstraintFinding> findings)
    {
        foreach (var volume in facts.DiskVolumes.Where(volume => volume.IsCritical))
        {
            findings.Add(new(
                ResourceConstraintSeverity.Critical,
                $"The filesystem containing {volume.Label} is critically low on space ({ResourceFactsPresentation.FormatBytes(volume.FreeBytes)} available)."));
        }
    }

    private static void AddIoFindings(ResourceFacts facts, List<ResourceConstraintFinding> findings)
    {
        if (facts.ContainerIoPressurePercent < 5)
            return;

        findings.Add(new(
            facts.ContainerIoPressurePercent >= 20 ? ResourceConstraintSeverity.Critical : ResourceConstraintSeverity.Warning,
            $"M3Undle tasks are stalling on storage I/O ({facts.ContainerIoPressurePercent:F1}% recent I/O pressure). This proves storage delay, but not whether the device is slow or another workload is contending for it."));
    }
}
