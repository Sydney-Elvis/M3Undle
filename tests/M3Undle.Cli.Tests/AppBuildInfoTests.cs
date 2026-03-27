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

        Assert.IsFalse(string.IsNullOrWhiteSpace(buildInfo.Version));
        Assert.AreNotEqual("unknown", buildInfo.Version);
        Assert.IsFalse(string.IsNullOrWhiteSpace(buildInfo.BuildDateUtc));
    }

    [TestMethod]
    public void ToDisplayString_IncludesBuildNumber_WhenPresent()
    {
        var buildInfo = new AppBuildInfo("test-version", "2026-03-27T12:00:00Z", "42");

        Assert.AreEqual(
            "test-version (build 42, built 2026-03-27T12:00:00Z)",
            buildInfo.ToDisplayString());
    }
}
