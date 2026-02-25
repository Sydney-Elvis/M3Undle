using M3Undle.Core.M3u;
using M3Undle.Web.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Providers;

[TestClass]
public sealed class ProviderValidationTests
{
    // -------------------------------------------------------------------------
    // NormalizeProviderChannelKey
    // -------------------------------------------------------------------------

    [TestMethod]
    public void NormalizeProviderChannelKey_Null_ReturnsNull()
        => Assert.IsNull(ProviderFetcher.NormalizeProviderChannelKey(null));

    [TestMethod]
    public void NormalizeProviderChannelKey_Empty_ReturnsNull()
        => Assert.IsNull(ProviderFetcher.NormalizeProviderChannelKey(""));

    [TestMethod]
    public void NormalizeProviderChannelKey_Whitespace_ReturnsNull()
        => Assert.IsNull(ProviderFetcher.NormalizeProviderChannelKey("   "));

    [TestMethod]
    public void NormalizeProviderChannelKey_Valid_ReturnsTrimmed()
        => Assert.AreEqual("cnn.us", ProviderFetcher.NormalizeProviderChannelKey("  cnn.us  "));

    [TestMethod]
    public void NormalizeProviderChannelKey_NoWhitespace_ReturnsSameValue()
        => Assert.AreEqual("espn.hd", ProviderFetcher.NormalizeProviderChannelKey("espn.hd"));

    // -------------------------------------------------------------------------
    // ParseEntry
    // -------------------------------------------------------------------------

    [TestMethod]
    public void ParseEntry_FullAttributes_ExtractsCorrectly()
    {
        var entry = new M3uEntry(
            ["#EXTINF:-1 tvg-id=\"cnn.us\" tvg-name=\"CNN\" tvg-logo=\"http://logos.com/cnn.png\" group-title=\"News\",CNN US"],
            "http://example.com/stream/cnn");

        var result = ProviderFetcher.ParseEntry(entry);

        Assert.AreEqual("cnn.us", result.ProviderChannelKey);
        Assert.AreEqual("CNN US", result.DisplayName);
        Assert.AreEqual("cnn.us", result.TvgId);
        Assert.AreEqual("CNN", result.TvgName);
        Assert.AreEqual("http://logos.com/cnn.png", result.LogoUrl);
        Assert.AreEqual("News", result.GroupTitle);
        Assert.AreEqual("http://example.com/stream/cnn", result.StreamUrl);
    }

    [TestMethod]
    public void ParseEntry_WhitespaceTvgId_ProducesNullKey()
    {
        var entry = new M3uEntry(
            ["#EXTINF:-1 tvg-id=\"   \" tvg-name=\"CNN\",CNN US"],
            "http://example.com/stream/cnn");

        var result = ProviderFetcher.ParseEntry(entry);

        Assert.IsNull(result.ProviderChannelKey);
        Assert.IsNull(result.TvgId);
    }

    [TestMethod]
    public void ParseEntry_EmptyTitle_FallsBackToTvgName()
    {
        var entry = new M3uEntry(
            ["#EXTINF:-1 tvg-id=\"cnn.us\" tvg-name=\"CNN\","],
            "http://example.com/stream/cnn");

        var result = ProviderFetcher.ParseEntry(entry);

        Assert.AreEqual("CNN", result.DisplayName);
    }

    [TestMethod]
    public void ParseEntry_NoTitleAndNoTvgName_UsesUnnamedChannel()
    {
        var entry = new M3uEntry(
            ["#EXTINF:-1,"],
            "http://example.com/stream/x");

        var result = ProviderFetcher.ParseEntry(entry);

        Assert.AreEqual("Unnamed Channel", result.DisplayName);
    }

    [TestMethod]
    public void ParseEntry_NoGroupTitle_GroupTitleIsNull()
    {
        var entry = new M3uEntry(
            ["#EXTINF:-1 tvg-id=\"espn.hd\",ESPN HD"],
            "http://example.com/stream/espn");

        var result = ProviderFetcher.ParseEntry(entry);

        Assert.IsNull(result.GroupTitle);
    }

    [TestMethod]
    public void ParseEntry_StreamUrlIsPreservedExact()
    {
        var url = "http://provider.example.com/live/stream?user=abc&pass=xyz&type=ts";
        var entry = new M3uEntry(
            ["#EXTINF:-1,Channel A"],
            url);

        var result = ProviderFetcher.ParseEntry(entry);

        Assert.AreEqual(url, result.StreamUrl);
    }

    // -------------------------------------------------------------------------
    // NormalizeStreamUrl
    // -------------------------------------------------------------------------

    [TestMethod]
    public void NormalizeStreamUrl_HttpsOnPort80_DowngradesToHttp()
    {
        var result = ProviderFetcher.NormalizeStreamUrl("https://provider.example.com:80/live/stream/1");
        Assert.AreEqual("http://provider.example.com:80/live/stream/1", result);
    }

    [TestMethod]
    public void NormalizeStreamUrl_HttpsOnPort443_Unchanged()
    {
        var url = "https://provider.example.com:443/live/stream/1";
        Assert.AreEqual(url, ProviderFetcher.NormalizeStreamUrl(url));
    }

    [TestMethod]
    public void NormalizeStreamUrl_HttpsNoExplicitPort_Unchanged()
    {
        var url = "https://provider.example.com/live/stream/1";
        Assert.AreEqual(url, ProviderFetcher.NormalizeStreamUrl(url));
    }

    [TestMethod]
    public void NormalizeStreamUrl_HttpOnPort80_Unchanged()
    {
        var url = "http://provider.example.com:80/live/stream/1";
        Assert.AreEqual(url, ProviderFetcher.NormalizeStreamUrl(url));
    }

    [TestMethod]
    public void NormalizeStreamUrl_NonHttpScheme_Unchanged()
    {
        var url = "rtmp://provider.example.com/live/stream";
        Assert.AreEqual(url, ProviderFetcher.NormalizeStreamUrl(url));
    }

    [TestMethod]
    public void NormalizeStreamUrl_PreservesQueryString()
    {
        var result = ProviderFetcher.NormalizeStreamUrl("https://provider.example.com:80/live/s?user=a&pass=b");
        Assert.AreEqual("http://provider.example.com:80/live/s?user=a&pass=b", result);
    }

    // -------------------------------------------------------------------------
    // ApplyHeadersFromJson
    // -------------------------------------------------------------------------

    [TestMethod]
    public void ApplyHeadersFromJson_Null_DoesNothing()
    {
        using var client = new HttpClient();
        ProviderFetcher.ApplyHeadersFromJson(client, null);
        Assert.AreEqual(0, client.DefaultRequestHeaders.Count());
    }

    [TestMethod]
    public void ApplyHeadersFromJson_EmptyString_DoesNothing()
    {
        using var client = new HttpClient();
        ProviderFetcher.ApplyHeadersFromJson(client, "");
        Assert.AreEqual(0, client.DefaultRequestHeaders.Count());
    }

    [TestMethod]
    public void ApplyHeadersFromJson_ValidJson_SetsHeader()
    {
        using var client = new HttpClient();
        ProviderFetcher.ApplyHeadersFromJson(client, "{\"X-Custom-Header\":\"test-value\"}");
        Assert.IsTrue(client.DefaultRequestHeaders.Contains("X-Custom-Header"));
        Assert.AreEqual("test-value", client.DefaultRequestHeaders.GetValues("X-Custom-Header").Single());
    }

    [TestMethod]
    public void ApplyHeadersFromJson_MultipleHeaders_AllApplied()
    {
        using var client = new HttpClient();
        ProviderFetcher.ApplyHeadersFromJson(client, "{\"X-Api-Key\":\"key1\",\"X-Version\":\"2\"}");
        Assert.IsTrue(client.DefaultRequestHeaders.Contains("X-Api-Key"));
        Assert.IsTrue(client.DefaultRequestHeaders.Contains("X-Version"));
    }

    [TestMethod]
    public void ApplyHeadersFromJson_ArrayJson_DoesNothing()
    {
        using var client = new HttpClient();
        ProviderFetcher.ApplyHeadersFromJson(client, "[\"invalid\"]");
        Assert.AreEqual(0, client.DefaultRequestHeaders.Count());
    }
}

