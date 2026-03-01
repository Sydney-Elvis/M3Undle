using M3Undle.Core.M3u;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Cli.Tests.M3u;

[TestClass]
public class LiveClassifierTests
{
    [TestMethod]
    public void IsLive_ReturnsTrue_ForLiveStreamUrls()
    {
        // ONYX-style live stream URLs
        Assert.IsTrue(LiveClassifier.IsLive("http://onyx.liveme.vip/live/abxy9r8t3u/9n6qzjp545/10016538.ts"));
        Assert.IsTrue(LiveClassifier.IsLive("http://onyx.liveme.vip/live/abxy9r8t3u/9n6qzjp545/10016593.ts"));
        
        // DELTA-style live stream URLs with port
        Assert.IsTrue(LiveClassifier.IsLive("https://pinkponyclub.online:443/70794108/47348826/13561"));
        Assert.IsTrue(LiveClassifier.IsLive("https://pinkponyclub.online:443/70794108/47348826/298629"));
        
        // Generic live URLs
        Assert.IsTrue(LiveClassifier.IsLive("http://provider.com/12345"));
        Assert.IsTrue(LiveClassifier.IsLive("https://provider.com/channel/123"));
    }

    [TestMethod]
    public void IsLive_ReturnsFalse_ForMovieUrls()
    {
        // ONYX-style movie URLs
        Assert.IsFalse(LiveClassifier.IsLive("http://onyx.liveme.vip/movie/abxy9r8t3u/9n6qzjp545/9028305.mkv"));
        Assert.IsFalse(LiveClassifier.IsLive("http://onyx.liveme.vip/movie/abxy9r8t3u/9n6qzjp545/9028306.mkv"));
        
        // DELTA-style movie URLs
        Assert.IsFalse(LiveClassifier.IsLive("https://pinkponyclub.online:443/movie/70794108/47348826/43456.mp4"));
        Assert.IsFalse(LiveClassifier.IsLive("https://pinkponyclub.online:443/movie/70794108/47348826/43957.mp4"));
        
        // Movies plural
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/movies/12345.mp4"));
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/vod/12345.mp4"));
    }

    [TestMethod]
    public void IsLive_ReturnsFalse_ForSeriesUrls()
    {
        // ONYX-style series URLs
        Assert.IsFalse(LiveClassifier.IsLive("http://onyx.liveme.vip/series/abxy9r8t3u/9n6qzjp545/9885725.mkv"));
        Assert.IsFalse(LiveClassifier.IsLive("http://onyx.liveme.vip/series/abxy9r8t3u/9n6qzjp545/9888930.mkv"));
        
        // DELTA-style series URLs
        Assert.IsFalse(LiveClassifier.IsLive("https://pinkponyclub.online:443/series/70794108/47348826/156012.mp4"));
        Assert.IsFalse(LiveClassifier.IsLive("https://pinkponyclub.online:443/series/70794108/47348826/149615.mp4"));
    }

    [TestMethod]
    public void IsLive_CaseInsensitive_ForVodSegments()
    {
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/MOVIE/12345.mp4"));
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/Movie/12345.mp4"));
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/SERIES/12345.mp4"));
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/Series/12345.mp4"));
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/Movies/12345.mp4"));
    }

    [TestMethod]
    public void IsLive_ReturnsFalse_ForQueryParametersWithVod()
    {
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/stream?type=vod&id=123"));
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/stream?type=movie&id=123"));
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/stream?type=series&id=123"));
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/stream?kind=vod&id=123"));
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/stream?kind=movie&id=123"));
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/stream?kind=series&id=123"));
    }

    [TestMethod]
    public void IsLive_HandlesNullAndEmpty()
    {
        Assert.IsFalse(LiveClassifier.IsLive(null));
        Assert.IsFalse(LiveClassifier.IsLive(""));
        Assert.IsFalse(LiveClassifier.IsLive("   "));
    }

    [TestMethod]
    public void IsLive_HandlesInvalidUris()
    {
        // Invalid URIs default to live if they don't contain VOD segments
        Assert.IsTrue(LiveClassifier.IsLive("not-a-valid-uri"));
        Assert.IsTrue(LiveClassifier.IsLive("12345"));
        
        // Invalid URIs with VOD segments are correctly filtered
        Assert.IsFalse(LiveClassifier.IsLive("not-a-valid-uri/movie/12345"));
        Assert.IsFalse(LiveClassifier.IsLive("something/series/episode1"));
    }

    [TestMethod]
    public void IsLive_HandlesEdgeCases()
    {
        // URL with movie in domain name should be live
        Assert.IsTrue(LiveClassifier.IsLive("http://movieprovider.com/channel123"));
        
        // URL with movie in query value but not in type/kind parameter should be live
        Assert.IsTrue(LiveClassifier.IsLive("http://provider.com/stream?name=movie"));
        
        // URL with series in fragment should be live
        Assert.IsTrue(LiveClassifier.IsLive("http://provider.com/stream#series"));
        
        // But actual path segments should be filtered
        Assert.IsFalse(LiveClassifier.IsLive("http://movieprovider.com/movie/12345"));
    }

    [TestMethod]
    public void IsLive_HandlesComplexRealWorldUrls()
    {
        // Real-world DELTA live stream
        Assert.IsTrue(LiveClassifier.IsLive("https://pinkponyclub.online:443/70794108/47348826/13561"));
        
        // Real-world DELTA movie
        Assert.IsFalse(LiveClassifier.IsLive("https://pinkponyclub.online:443/movie/70794108/47348826/43456.mp4"));
        
        // Real-world DELTA series
        Assert.IsFalse(LiveClassifier.IsLive("https://pinkponyclub.online:443/series/70794108/47348826/156012.mp4"));
        
        // Real-world ONYX live stream
        Assert.IsTrue(LiveClassifier.IsLive("http://onyx.liveme.vip/live/abxy9r8t3u/9n6qzjp545/10016538.ts"));
        
        // Real-world ONYX movie
        Assert.IsFalse(LiveClassifier.IsLive("http://onyx.liveme.vip/movie/abxy9r8t3u/9n6qzjp545/9028305.mkv"));
        
        // Real-world ONYX series
        Assert.IsFalse(LiveClassifier.IsLive("http://onyx.liveme.vip/series/abxy9r8t3u/9n6qzjp545/9885725.mkv"));
    }

    [TestMethod]
    public void IsLive_UsesExtensionHeuristics_WhenNoExplicitTypeSegment()
    {
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/u/p/12345.mp4"));
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/u/p/12345.mkv"));
        Assert.IsTrue(LiveClassifier.IsLive("http://provider.com/u/p/12345.ts"));
        Assert.IsTrue(LiveClassifier.IsLive("http://provider.com/u/p/12345.m3u8"));
    }

    [TestMethod]
    public void IsLive_DetectsExplicitLiveMarkers()
    {
        Assert.IsTrue(LiveClassifier.IsLive("http://provider.com/live/u/p/12345"));
        Assert.IsTrue(LiveClassifier.IsLive("http://provider.com/play?id=123&type=live"));
    }

    [TestMethod]
    public void IsLive_HandlesUrlEncodedParameters()
    {
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/stream?type=vod&name=%20test"));
        Assert.IsFalse(LiveClassifier.IsLive("http://provider.com/stream?kind=movie&id=%2F123"));
    }
}
