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
}
