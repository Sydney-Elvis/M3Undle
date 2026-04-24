using M3Undle.Core.M3u;
using M3Undle.Core.Providers;

namespace M3Undle.Core.Tests.Providers;

[TestClass]
public sealed class ProviderChannelNormalizerTests
{
    [TestMethod]
    public void NormalizeProviderChannelKey_Null_ReturnsNull()
        => Assert.IsNull(ProviderChannelNormalizer.NormalizeProviderChannelKey(null));

    [TestMethod]
    public void NormalizeProviderChannelKey_Empty_ReturnsNull()
        => Assert.IsNull(ProviderChannelNormalizer.NormalizeProviderChannelKey(""));

    [TestMethod]
    public void NormalizeProviderChannelKey_Whitespace_ReturnsNull()
        => Assert.IsNull(ProviderChannelNormalizer.NormalizeProviderChannelKey("   "));

    [TestMethod]
    public void NormalizeProviderChannelKey_Valid_ReturnsTrimmed()
        => Assert.AreEqual("cnn.us", ProviderChannelNormalizer.NormalizeProviderChannelKey("  cnn.us  "));

    [TestMethod]
    public void NormalizeProviderChannelKey_NoWhitespace_ReturnsSameValue()
        => Assert.AreEqual("espn.hd", ProviderChannelNormalizer.NormalizeProviderChannelKey("espn.hd"));

    [TestMethod]
    public void ParseEntry_FullAttributes_ExtractsCorrectly()
    {
        var entry = new M3uEntry(
            ["#EXTINF:-1 tvg-id=\"cnn.us\" tvg-name=\"CNN\" tvg-logo=\"http://logos.com/cnn.png\" group-title=\"News\",CNN US"],
            "http://example.com/stream/cnn");

        var result = ProviderChannelNormalizer.ParseEntry(entry);

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

        var result = ProviderChannelNormalizer.ParseEntry(entry);

        Assert.IsNull(result.ProviderChannelKey);
        Assert.IsNull(result.TvgId);
    }

    [TestMethod]
    public void ParseEntry_EmptyTitle_FallsBackToTvgName()
    {
        var entry = new M3uEntry(
            ["#EXTINF:-1 tvg-id=\"cnn.us\" tvg-name=\"CNN\","],
            "http://example.com/stream/cnn");

        var result = ProviderChannelNormalizer.ParseEntry(entry);

        Assert.AreEqual("CNN", result.DisplayName);
    }

    [TestMethod]
    public void ParseEntry_NoTitleAndNoTvgName_UsesUnnamedChannel()
    {
        var entry = new M3uEntry(
            ["#EXTINF:-1,"],
            "http://example.com/stream/x");

        var result = ProviderChannelNormalizer.ParseEntry(entry);

        Assert.AreEqual("Unnamed Channel", result.DisplayName);
    }

    [TestMethod]
    public void ParseEntry_NoGroupTitle_GroupTitleIsNull()
    {
        var entry = new M3uEntry(
            ["#EXTINF:-1 tvg-id=\"espn.hd\",ESPN HD"],
            "http://example.com/stream/espn");

        var result = ProviderChannelNormalizer.ParseEntry(entry);

        Assert.IsNull(result.GroupTitle);
    }

    [TestMethod]
    public void ParseEntry_DataUriLogoWithComma_DoesNotCorruptDisplayName()
    {
        var entry = new M3uEntry(
            ["#EXTINF:-1 tvg-id=\"10.comedy\" tvg-logo=\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUg\" group-title=\"Australia | Locals (Test)\",10 Comedy"],
            "http://example.com/stream/10comedy");

        var result = ProviderChannelNormalizer.ParseEntry(entry);

        Assert.AreEqual("10 Comedy", result.DisplayName);
        Assert.AreEqual("data:image/png;base64,iVBORw0KGgoAAAANSUhEUg", result.LogoUrl);
        Assert.AreEqual("Australia | Locals (Test)", result.GroupTitle);
    }

    [TestMethod]
    public void ParseEntry_MalformedDuplicatedMetadata_PicksCleanTitle()
    {
        var entry = new M3uEntry(
            ["#EXTINF:-1 xui-id=\"{XUI_ID}\" tvg-id=\"C1353.300.ersatztv.org\" tvg-name=\"M.E.\" tvg-logo=\"https://i.imgur.com/9I4Sa2K.png\" group-title=\"Action & Crime\",Quincy, M.E.\" tvg-logo=\"https://i.imgur.com/9I4Sa2K.png\" group-title=\"24/7 | N-R\",M.E.\" tvg-logo=\"https://i.imgur.com/9I4Sa2K.png\" group-title=\"Action & Crime\",Quincy, M.E."],
            "http://example.com/stream/quincy");

        var result = ProviderChannelNormalizer.ParseEntry(entry);

        Assert.AreEqual("Quincy, M.E.", result.DisplayName);
        Assert.AreEqual("Action & Crime", result.GroupTitle);
    }

    [TestMethod]
    public void ParseEntry_StreamUrlIsPreservedExact()
    {
        var url = "http://provider.example.com/live/stream?user=abc&pass=xyz&type=ts";
        var entry = new M3uEntry(
            ["#EXTINF:-1,Channel A"],
            url);

        var result = ProviderChannelNormalizer.ParseEntry(entry);

        Assert.AreEqual(url, result.StreamUrl);
    }

    [TestMethod]
    public void NormalizeStreamUrl_HttpsOnPort80_DowngradesToHttp()
    {
        var result = ProviderChannelNormalizer.NormalizeStreamUrl("https://provider.example.com:80/live/stream/1");
        Assert.AreEqual("http://provider.example.com:80/live/stream/1", result);
    }

    [TestMethod]
    public void NormalizeStreamUrl_HttpsOnPort443_Unchanged()
    {
        var url = "https://provider.example.com:443/live/stream/1";
        Assert.AreEqual(url, ProviderChannelNormalizer.NormalizeStreamUrl(url));
    }

    [TestMethod]
    public void NormalizeStreamUrl_HttpsNoExplicitPort_Unchanged()
    {
        var url = "https://provider.example.com/live/stream/1";
        Assert.AreEqual(url, ProviderChannelNormalizer.NormalizeStreamUrl(url));
    }

    [TestMethod]
    public void NormalizeStreamUrl_HttpOnPort80_Unchanged()
    {
        var url = "http://provider.example.com:80/live/stream/1";
        Assert.AreEqual(url, ProviderChannelNormalizer.NormalizeStreamUrl(url));
    }

    [TestMethod]
    public void NormalizeStreamUrl_NonHttpScheme_Unchanged()
    {
        var url = "rtmp://provider.example.com/live/stream";
        Assert.AreEqual(url, ProviderChannelNormalizer.NormalizeStreamUrl(url));
    }

    [TestMethod]
    public void NormalizeStreamUrl_PreservesQueryString()
    {
        var result = ProviderChannelNormalizer.NormalizeStreamUrl("https://provider.example.com:80/live/s?user=a&pass=b");
        Assert.AreEqual("http://provider.example.com:80/live/s?user=a&pass=b", result);
    }
}
