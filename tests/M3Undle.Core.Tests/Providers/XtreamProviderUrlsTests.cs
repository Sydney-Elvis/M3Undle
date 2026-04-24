using M3Undle.Core.Providers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Core.Tests.Providers;

[TestClass]
public sealed class XtreamProviderUrlsTests
{
    [TestMethod]
    public void BuildPlaylistUrl_BuildsXtreamGetUrl()
    {
        var url = XtreamProviderUrls.BuildPlaylistUrl(
            "https://panel.example.test",
            "viewer",
            "secret");

        Assert.AreEqual(
            "https://panel.example.test/get.php?username=viewer&password=secret&type=m3u_plus&output=ts",
            url);
    }

    [TestMethod]
    public void BuildPlaylistUrl_UrlEncodesCredentials()
    {
        var url = XtreamProviderUrls.BuildPlaylistUrl(
            "https://panel.example.test",
            "user name",
            "p@ss word&more");

        Assert.AreEqual(
            "https://panel.example.test/get.php?username=user%20name&password=p%40ss%20word%26more&type=m3u_plus&output=ts",
            url);
    }

    [TestMethod]
    public void BuildPlaylistUrl_NullUsername_UsesEmptyUsername()
    {
        var url = XtreamProviderUrls.BuildPlaylistUrl(
            "https://panel.example.test",
            null,
            "secret");

        Assert.AreEqual(
            "https://panel.example.test/get.php?username=&password=secret&type=m3u_plus&output=ts",
            url);
    }

    [TestMethod]
    public void BuildPlaylistUrl_PreservesBaseUrlAsProvided()
    {
        var url = XtreamProviderUrls.BuildPlaylistUrl(
            "https://panel.example.test/",
            "viewer",
            "secret");

        Assert.AreEqual(
            "https://panel.example.test//get.php?username=viewer&password=secret&type=m3u_plus&output=ts",
            url);
    }

    [TestMethod]
    public void BuildXmltvUrl_BuildsXtreamXmltvUrl()
    {
        var url = XtreamProviderUrls.BuildXmltvUrl(
            "https://panel.example.test",
            "viewer",
            "secret");

        Assert.AreEqual(
            "https://panel.example.test/xmltv.php?username=viewer&password=secret",
            url);
    }

    [TestMethod]
    public void BuildXmltvUrl_UrlEncodesCredentials()
    {
        var url = XtreamProviderUrls.BuildXmltvUrl(
            "https://panel.example.test",
            "user name",
            "p@ss word&more");

        Assert.AreEqual(
            "https://panel.example.test/xmltv.php?username=user%20name&password=p%40ss%20word%26more",
            url);
    }
}
