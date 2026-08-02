using System.Globalization;

namespace M3Undle.Web.Observability.Resources;

// Pure parsers over Linux pseudo-file content — kept separate from any file I/O so they can
// be unit-tested against synthetic content copied from the cgroup v2 / procfs documented
// formats, independent of whether this is running on Linux at all.
internal static class LinuxResourceParsers
{
    // cgroup v2 cpu.stat, e.g.:
    //   usage_usec 4353342314
    //   user_usec 4123456789
    //   system_usec 229885525
    //   nr_periods 24728
    //   nr_throttled 71
    //   throttled_usec 1520000
    public readonly record struct CpuStat(long UsageUsec, long NrThrottled, long ThrottledUsec);

    public static CpuStat? ParseCgroupCpuStat(string content)
    {
        long? usageUsec = null, nrThrottled = null, throttledUsec = null;
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                continue;

            switch (parts[0])
            {
                case "usage_usec": usageUsec = value; break;
                case "nr_throttled": nrThrottled = value; break;
                case "throttled_usec": throttledUsec = value; break;
            }
        }

        return usageUsec is { } u && nrThrottled is { } n && throttledUsec is { } t
            ? new CpuStat(u, n, t)
            : null;
    }

    // /proc/stat aggregate "cpu" line: user nice system idle iowait irq softirq steal guest
    // guest_nice. guest/guest_nice are already counted within user/nice per kernel docs, so
    // total time is the sum of the first eight fields only — this matches how tools like
    // `top`/`mpstat` compute total CPU time, avoiding double-counting.
    public readonly record struct ProcStatCpu(long User, long Nice, long System, long Idle, long IoWait, long Irq, long SoftIrq, long Steal)
    {
        public long Total => User + Nice + System + Idle + IoWait + Irq + SoftIrq + Steal;
    }

    public static ProcStatCpu? ParseProcStatAggregateCpuLine(string content)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 9 || parts[0] != "cpu")
                continue;

            var values = new long[8];
            for (var i = 0; i < 8; i++)
            {
                if (!long.TryParse(parts[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                    return null;
            }

            return new ProcStatCpu(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]);
        }

        return null;
    }

    // /proc/loadavg: "0.52 0.58 0.59 1/234 5678" — 1-min, 5-min, 15-min load, running/total
    // processes, last PID. Only the 1-minute figure is used today.
    public static double? ParseLoadAverage1Min(string content)
    {
        var parts = content.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var load)
            ? load
            : null;
    }
}
