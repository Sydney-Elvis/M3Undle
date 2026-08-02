namespace M3Undle.Web.Observability.Resources;

// The footer stays CPU-scoped by design (that's the one number worth glancing at from any
// page); memory/disk/egress live on the details page only. Shared so the footer chip's
// tooltip and any other summary surface never drift out of sync.
public static class ResourceFactsPresentation
{
    public static string BuildFooterSummarySentence(ResourceFacts facts)
    {
        if (facts.ContainerCpuPercent is { } containerCpu)
            return $"M3Undle is using {containerCpu:F0}% of its container's CPU limit.";

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
        if (bytesPerSecond <= 0) return "—";
        var bitsPerSec = bytesPerSecond * 8;
        return bitsPerSec >= 1_000_000
            ? $"{bitsPerSec / 1_000_000.0:F1} Mbps"
            : $"{bitsPerSec / 1_000.0:F0} Kbps";
    }
}
