using M3Undle.Core;

namespace M3Undle.Core.Tests;

[TestClass]
public sealed class AppBuildInfoTests
{
    [TestMethod]
    public void ToDisplayString_IncludesBuildNumber_WhenPresent()
    {
        var buildInfo = new AppBuildInfo("test-version", "2026-03-27T12:00:00Z", "42", null);

        Assert.AreEqual(
            "test-version (build 42, built 2026-03-27T12:00:00Z)",
            buildInfo.ToDisplayString());
    }
}
