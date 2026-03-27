using M3Undle.Cli;
using M3Undle.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Cli.Tests;

[TestClass]
public sealed class AppBuildInfoTests
{
    [TestMethod]
    public void FromAssembly_ReadsVersionAndBuildDate_FromCliAssembly()
    {
        var buildInfo = AppBuildInfo.FromAssembly(typeof(CliApp).Assembly);

        Assert.AreEqual("1.0.0-alpha.4", buildInfo.Version);
        Assert.IsFalse(string.IsNullOrWhiteSpace(buildInfo.BuildDateUtc));
    }

    [TestMethod]
    public void ToDisplayString_IncludesBuildNumber_WhenPresent()
    {
        var buildInfo = new AppBuildInfo("1.0.0-alpha.4", "2026-03-27T12:00:00Z", "42");

        Assert.AreEqual(
            "1.0.0-alpha.4 (build 42, built 2026-03-27T12:00:00Z)",
            buildInfo.ToDisplayString());
    }
}
