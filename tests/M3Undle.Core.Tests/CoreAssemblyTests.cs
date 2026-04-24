using M3Undle.Core.M3u;

namespace M3Undle.Core.Tests;

[TestClass]
public sealed class CoreAssemblyTests
{
    [TestMethod]
    public void CoreTestProject_ReferencesCoreAssemblyOnly()
    {
        Assert.AreEqual("M3Undle.Core", typeof(PlaylistParser).Assembly.GetName().Name);
    }
}
