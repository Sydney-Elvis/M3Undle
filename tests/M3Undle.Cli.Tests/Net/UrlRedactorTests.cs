using System;
using M3Undle.Core.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Cli.Tests.Net;

[TestClass]
public class UrlRedactorTests
{
    [TestMethod]
    public void RedactUrl_RemovesQueryString()
    {
        var url = "http://host.com/get.php?username=user&password=secret";
        var result = UrlRedactor.RedactUrl(url);
        Assert.AreEqual("http://host.com/get.php", result);
    }

    [TestMethod]
    public void RedactUrl_RemovesFragment()
    {
        var url = "http://host.com/page#section";
        var result = UrlRedactor.RedactUrl(url);
        Assert.AreEqual("http://host.com/page", result);
    }

    [TestMethod]
    public void RedactUrl_PreservesPath()
    {
        var url = "https://host.com/api/v1/playlist.m3u?token=secret";
        var result = UrlRedactor.RedactUrl(url);
        Assert.AreEqual("https://host.com/api/v1/playlist.m3u", result);
    }

    [TestMethod]
    public void RedactUrl_PreservesPort()
    {
        var url = "http://host.com:8080/api?key=secret";
        var result = UrlRedactor.RedactUrl(url);
        Assert.AreEqual("http://host.com:8080/api", result);
    }

    [TestMethod]
    public void RedactUrl_HandlesHttps()
    {
        var url = "https://secure.com/data?auth=token";
        var result = UrlRedactor.RedactUrl(url);
        Assert.AreEqual("https://secure.com/data", result);
    }

    [TestMethod]
    public void RedactUrl_HandlesComplexQueryString()
    {
        var url = "http://host.com/get.php?username=user&password=pass&type=m3u_plus&output=ts";
        var result = UrlRedactor.RedactUrl(url);
        Assert.AreEqual("http://host.com/get.php", result);
    }

    [TestMethod]
    public void RedactUrl_HandlesUrlWithoutQueryString()
    {
        var url = "http://host.com/playlist.m3u";
        var result = UrlRedactor.RedactUrl(url);
        Assert.AreEqual("http://host.com/playlist.m3u", result);
    }

    [TestMethod]
    public void RedactUrl_HandlesEmptyString()
    {
        var result = UrlRedactor.RedactUrl(string.Empty);
        Assert.AreEqual(string.Empty, result);
    }

    [TestMethod]
    public void RedactUrl_HandlesNull()
    {
        var result = UrlRedactor.RedactUrl((string)null!);
        Assert.AreEqual(string.Empty, result);
    }

    [TestMethod]
    public void RedactUrl_HandlesFilePath()
    {
        var path = "/path/to/file.m3u";
        var result = UrlRedactor.RedactUrl(path);
        // On Unix-like systems, /path/to/file.m3u is treated as an absolute file URI
        // and becomes file:///path/to/file.m3u
        // We should accept either the original path or the file URI
        var isValidResult = result == path || result == "file:///path/to/file.m3u";
        Assert.IsTrue(isValidResult, $"Expected either '{path}' or 'file:///path/to/file.m3u', but got '{result}'");
    }

    [TestMethod]
    public void RedactUrl_WithUri_RemovesQueryString()
    {
        var uri = new Uri("http://host.com/get.php?username=user&password=secret");
        var result = UrlRedactor.RedactUrl(uri);
        Assert.AreEqual("http://host.com/get.php", result);
    }

    [TestMethod]
    public void RedactUrl_WithUri_HandlesNull()
    {
        var result = UrlRedactor.RedactUrl((Uri)null!);
        Assert.AreEqual(string.Empty, result);
    }
}

