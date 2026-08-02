using M3Undle.Web.Observability.Resources;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Observability.Resources;

[TestClass]
public sealed class LinuxResourceParsersTests
{
    [TestMethod]
    public void ParseCgroupCpuStat_WellFormedContent_ParsesAllFields()
    {
        const string content = """
            usage_usec 4353342314
            user_usec 4123456789
            system_usec 229885525
            nr_periods 24728
            nr_throttled 71
            throttled_usec 1520000
            """;

        var result = LinuxResourceParsers.ParseCgroupCpuStat(content);

        Assert.IsNotNull(result);
        Assert.AreEqual(4353342314L, result.Value.UsageUsec);
        Assert.AreEqual(71L, result.Value.NrThrottled);
        Assert.AreEqual(1520000L, result.Value.ThrottledUsec);
    }

    [TestMethod]
    public void ParseCgroupCpuStat_MissingRequiredField_ReturnsNull()
    {
        const string content = """
            usage_usec 4353342314
            user_usec 4123456789
            system_usec 229885525
            nr_periods 24728
            """;

        var result = LinuxResourceParsers.ParseCgroupCpuStat(content);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ParseCgroupCpuStat_EmptyContent_ReturnsNull()
    {
        var result = LinuxResourceParsers.ParseCgroupCpuStat(string.Empty);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ParseProcStatAggregateCpuLine_WellFormedContent_SumsFirstEightFieldsOnly()
    {
        const string content = """
            cpu  123 45 67 8901 12 3 4 5 0 0
            cpu0 60 20 30 4450 6 1 2 2 0 0
            intr 12345 0 0 0
            """;

        var result = LinuxResourceParsers.ParseProcStatAggregateCpuLine(content);

        Assert.IsNotNull(result);
        Assert.AreEqual(123L, result.Value.User);
        Assert.AreEqual(45L, result.Value.Nice);
        Assert.AreEqual(67L, result.Value.System);
        Assert.AreEqual(8901L, result.Value.Idle);
        Assert.AreEqual(12L, result.Value.IoWait);
        Assert.AreEqual(3L, result.Value.Irq);
        Assert.AreEqual(4L, result.Value.SoftIrq);
        Assert.AreEqual(5L, result.Value.Steal);
        // guest/guest_nice (the trailing "0 0") are already counted within user/nice per
        // kernel docs, so Total must be the sum of only the first eight fields.
        Assert.AreEqual(123L + 45 + 67 + 8901 + 12 + 3 + 4 + 5, result.Value.Total);
    }

    [TestMethod]
    public void ParseProcStatAggregateCpuLine_NoCpuLine_ReturnsNull()
    {
        const string content = """
            intr 12345 0 0 0
            ctxt 98765
            """;

        var result = LinuxResourceParsers.ParseProcStatAggregateCpuLine(content);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ParseLoadAverage1Min_WellFormedContent_ReturnsFirstValue()
    {
        var result = LinuxResourceParsers.ParseLoadAverage1Min("0.52 0.58 0.59 1/234 5678\n");

        Assert.AreEqual(0.52, result);
    }

    [TestMethod]
    public void ParseLoadAverage1Min_EmptyContent_ReturnsNull()
    {
        var result = LinuxResourceParsers.ParseLoadAverage1Min(string.Empty);

        Assert.IsNull(result);
    }
}
