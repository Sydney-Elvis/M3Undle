namespace M3Undle.Web.Observability.Resources;

// The footer stays CPU-scoped by design (that's the one number worth glancing at from any
// page); memory/disk/egress live on the details page only. Shared so the footer chip's
// tooltip and any other summary surface never drift out of sync.
public static class ResourceFactsPresentation
{
    public static string BuildFooterSummarySentence(ResourceFacts facts)
    {
        var usage = BuildCpuUsageSentence(facts);
        var diagnosis = ResourceConstraintDiagnosis.EvaluateCpu(facts)
            .OrderByDescending(finding => finding.Severity)
            .FirstOrDefault();
        return diagnosis is null ? usage : $"{usage} {diagnosis.Message}";
    }

    public static ResourceConstraintSeverity GetFooterCpuSeverity(ResourceFacts facts)
    {
        var findings = ResourceConstraintDiagnosis.EvaluateCpu(facts);
        return findings.Any(finding => finding.Severity == ResourceConstraintSeverity.Critical)
            ? ResourceConstraintSeverity.Critical
            : findings.Any(finding => finding.Severity == ResourceConstraintSeverity.Warning)
                ? ResourceConstraintSeverity.Warning
                : ResourceConstraintSeverity.Information;
    }

    private static string BuildCpuUsageSentence(ResourceFacts facts)
    {
        if (facts.ContainerCpuPercent is { } containerCpu)
            return facts.ContainerCpuLimitCores is { } limit
                ? $"M3Undle is using {containerCpu:F0}% of its {limit:F2}-CPU container allocation."
                : $"M3Undle is using {containerCpu:F0}% of the CPU capacity visible to its container.";

        if (facts.ProcessCpuPercent is { } processCpu)
            return $"M3Undle's process is currently using {processCpu:F0}% CPU.";

        return "CPU usage data is still warming up.";
    }

    public static string FormatFooterLabel(ResourceFacts facts)
    {
        var value = facts.ContainerCpuPercent ?? facts.ProcessCpuPercent;
        return value is { } v ? $"CPU {v:F0}%" : "CPU —";
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024 => $"{bytes / 1_024.0:F1} KB",
        _ => $"{bytes} B",
    };

    public static string FormatBitrate(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0 bps";
        var bitsPerSec = bytesPerSecond * 8;
        return bitsPerSec >= 1_000_000
            ? $"{bitsPerSec / 1_000_000.0:F1} Mbps"
            : $"{bitsPerSec / 1_000.0:F0} Kbps";
    }

    public static string FormatUsedCapacity(long usedBytes, long totalBytes) => totalBytes > 0
        ? $"{FormatBytes(usedBytes)} used of {FormatBytes(totalBytes)} ({usedBytes * 100.0 / totalBytes:F0}%)"
        : $"{FormatBytes(usedBytes)} used";
}
