using System.Net;
using M3Undle.Web.Streaming.Observability;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class RemoteIpAddressFormatterTests
{
    [TestMethod]
    public void Format_MapsIpv4MappedIpv6ToIpv4()
    {
        var result = RemoteIpAddressFormatter.Format(IPAddress.Parse("::ffff:192.168.1.100"));

        Assert.AreEqual("192.168.1.100", result);
    }

    [TestMethod]
    public void Format_PreservesNativeIpv6()
    {
        var result = RemoteIpAddressFormatter.Format(IPAddress.Parse("2001:db8::1"));

        Assert.AreEqual("2001:db8::1", result);
    }

    [TestMethod]
    public void Format_PreservesUnparseableValue()
    {
        Assert.AreEqual("unknown", RemoteIpAddressFormatter.Format("unknown"));
    }
}
