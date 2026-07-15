using M3Undle.Web.Streaming.Observability;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class ClientUserAgentResolverTests
{
    [TestMethod]
    [DataRow("SparkleTV/2.3.1 (SmartTV 4K, Android 11)", "SparkleTV", 2)]
    [DataRow("SmartersPro/1.0 (Linux; Android 12)", "SmartersPro", 2)]
    [DataRow("Smarters Pro/1.0", "Smarters Pro", 2)]
    [DataRow("IPTV Smarters/4.0", "IPTV Smarters", 2)]
    [DataRow("TiviMate/5.1.6", "TiviMate", 2)]
    [DataRow("Jellyfin/10.10.7", "Jellyfin", 2)]
    [DataRow("UnknownPlayer/4.2 (Linux)", "UnknownPlayer", 2)]
    [DataRow("Lavf/60.16.100", "Lavf", 2)]
    [DataRow("ExoPlayerLib/2.19.1", "ExoPlayer", 1)]
    [DataRow("Dalvik/2.1.0 (Linux; Android 11; SmartTV 4K Build/RTT0)", "Android TV", 1)]
    [DataRow("okhttp/4.12.0", "Android App", 1)]
    [DataRow("Mozilla/5.0 (Linux) AppleWebKit/537.36 Chrome/124.0 Safari/537.36", "Browser", 1)]
    public void Resolve_UsesHeaderProductAndAliases(string userAgent, string expectedName, int expectedSpecificity)
    {
        var result = ClientUserAgentResolver.Resolve(userAgent);

        Assert.AreEqual(expectedName, result.DisplayName);
        Assert.AreEqual(expectedSpecificity, result.Specificity);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("(device comment only)")]
    public void Resolve_WhenNoProductIsAvailable_ReturnsUnknown(string? userAgent)
    {
        var result = ClientUserAgentResolver.Resolve(userAgent);

        Assert.AreEqual("Unknown", result.DisplayName);
        Assert.AreEqual(0, result.Specificity);
    }
}
