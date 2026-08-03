using System.Globalization;

namespace M3Undle.Web.Observability.Resources;

// Pure parsers over Linux pseudo-file content — kept separate from any file I/O so they can
// be unit-tested against synthetic content copied from the cgroup v2 / procfs documented
// formats, independent of whether this is running on Linux at all.
internal static class LinuxResourceParsers
{
    public readonly record struct CpuLimit(double? Cores);

    public static CpuLimit? ParseCgroupCpuMax(string content)
    {
        var parts = content.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var period)
            || period <= 0)
            return null;

        if (parts[0] == "max")
            return new CpuLimit(null);

        return long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quota) && quota > 0
            ? new CpuLimit((double)quota / period)
            : null;
    }

    public static long? ParseCgroupByteValue(string content)
    {
        var value = content.Trim();
        if (value == "max")
            return null;

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes >= 0
            ? bytes
            : null;
    }

    public sealed record MemoryEvents(long High, long Max, long Oom, long OomKill);

    public static MemoryEvents? ParseMemoryEvents(string content)
    {
        var values = ParseKeyValueLines(content);
        return values.TryGetValue("high", out var high)
            && values.TryGetValue("max", out var max)
            && values.TryGetValue("oom", out var oom)
            && values.TryGetValue("oom_kill", out var oomKill)
            ? new MemoryEvents(high, max, oom, oomKill)
            : null;
    }

    public static double? ParsePressureSomeAverage10(string content)
    {
        var someLine = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("some ", StringComparison.Ordinal));
        var average = someLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith("avg10=", StringComparison.Ordinal));

        return average is not null
            && double.TryParse(average.AsSpan(6), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static Dictionary<string, long> ParseKeyValueLines(string content)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                values[parts[0]] = value;
        }

        return values;
    }

    // cgroup v2 cpu.stat, e.g.:
    //   usage_usec 4353342314
    //   user_usec 4123456789
    //   system_usec 229885525
    //   nr_periods 24728
    //   nr_throttled 71
    //   throttled_usec 1520000
    public readonly record struct CpuStat(long UsageUsec, long NrPeriods, long NrThrottled, long ThrottledUsec);

    public static CpuStat? ParseCgroupCpuStat(string content)
    {
        long? usageUsec = null, nrPeriods = null, nrThrottled = null, throttledUsec = null;
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                continue;

            switch (parts[0])
            {
                case "usage_usec": usageUsec = value; break;
                case "nr_periods": nrPeriods = value; break;
                case "nr_throttled": nrThrottled = value; break;
                case "throttled_usec": throttledUsec = value; break;
            }
        }

        return usageUsec is { } u && nrPeriods is { } p && nrThrottled is { } n && throttledUsec is { } t
            ? new CpuStat(u, p, n, t)
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

    // /proc/loadavg: "0.52 0.58 0.59 1/234 5678" — 1-min, 5-min, 15-min load,
    // running/total processes, and last PID.
    public readonly record struct LoadAverage(double OneMinute, double FiveMinutes, double FifteenMinutes);

    public static LoadAverage? ParseLoadAverage(string content)
    {
        var parts = content.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var one)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var five)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var fifteen)
            ? new LoadAverage(one, five, fifteen)
            : null;
    }
}
