using M3Undle.Web.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.HdHomeRun;

[TestClass]
public sealed class HdHomeRunLineupServiceTests
{
    [TestMethod]
    public async Task TryBuildActiveLineupAsync_ReturnsOnlyLiveChannelsInStableOrder()
    {
        var lineup = new RenderedLineup(
            SnapshotId: "snapshot-1",
            ProfileId: "profile-1",
            SnapshotCreatedUtc: DateTime.UtcNow,
            ChannelIndexPath: "unused",
            XmltvPath: null,
            Channels:
            [
                new RenderedLineupChannel("live-1", "Alpha", "alpha.tv", "Alpha", null, "News", 11, "http://example.com/live/alpha.ts", "live"),
                new RenderedLineupChannel("vod-1", "Movie One", "movie.one", null, null, "Movies", null, "http://example.com/movie/one.mkv", "vod"),
                new RenderedLineupChannel("live-2", "Bravo", "bravo.tv", "Bravo Name", null, "News", null, "http://example.com/live/bravo.ts", "live"),
            ]);

        var service = new HdHomeRunLineupService();
        var context = new DefaultHttpContext();

        var result = await service.TryBuildActiveLineupAsync("http://test-host:8080", lineup, context, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.HasCount(2, result.Channels);

        var first = result.Channels[0];
        var second = result.Channels[1];

        Assert.AreEqual("live-1", first.ChannelId);
        Assert.AreEqual("11", first.GuideNumber);
        Assert.AreEqual("Alpha", first.GuideName);
        Assert.AreEqual("http://test-host:8080/hdhr/auto/v11", first.Url);

        Assert.AreEqual("live-2", second.ChannelId);
        Assert.AreEqual("1000", second.GuideNumber);
        Assert.AreEqual("Bravo", second.GuideName);
        Assert.AreEqual("http://test-host:8080/hdhr/auto/v1000", second.Url);
    }

    [TestMethod]
    public async Task TryBuildActiveLineupAsync_NoLiveChannels_ReturnsEmptyList()
    {
        var lineup = new RenderedLineup(
            SnapshotId: "snapshot-1",
            ProfileId: "profile-1",
            SnapshotCreatedUtc: DateTime.UtcNow,
            ChannelIndexPath: "unused",
            XmltvPath: null,
            Channels:
            [
                new RenderedLineupChannel("vod-1", "Movie One", "movie.one", null, null, "Movies", null, "http://example.com/movie/one.mkv", "vod"),
            ]);

        var service = new HdHomeRunLineupService();
        var context = new DefaultHttpContext();

        var result = await service.TryBuildActiveLineupAsync("http://test-host:8080", lineup, context, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsEmpty(result.Channels);
    }

    [TestMethod]
    public async Task TryBuildActiveLineupAsync_MixedInputOrder_ReturnsGloballySortedGuideNumbers()
    {
        var lineup = new RenderedLineup(
            SnapshotId: "snapshot-1",
            ProfileId: "profile-1",
            SnapshotCreatedUtc: DateTime.UtcNow,
            ChannelIndexPath: "unused",
            XmltvPath: null,
            Channels:
            [
                new RenderedLineupChannel("live-129", "Ch 129", "ch129", null, null, "A", 129, "http://example.com/live/129.ts", "live"),
                new RenderedLineupChannel("live-130", "Ch 130", "ch130", null, null, "A", 130, "http://example.com/live/130.ts", "live"),
                new RenderedLineupChannel("live-131", "Ch 131", "ch131", null, null, "A", 131, "http://example.com/live/131.ts", "live"),
                new RenderedLineupChannel("live-103", "Ch 103", "ch103", null, null, "B", 103, "http://example.com/live/103.ts", "live"),
                new RenderedLineupChannel("live-100", "Ch 100", "ch100", null, null, "B", 100, "http://example.com/live/100.ts", "live"),
                new RenderedLineupChannel("live-104", "Ch 104", "ch104", null, null, "B", 104, "http://example.com/live/104.ts", "live"),
                new RenderedLineupChannel("live-102", "Ch 102", "ch102", null, null, "B", 102, "http://example.com/live/102.ts", "live"),
                new RenderedLineupChannel("live-101", "Ch 101", "ch101", null, null, "B", 101, "http://example.com/live/101.ts", "live"),
                new RenderedLineupChannel("live-105", "Ch 105", "ch105", null, null, "B", 105, "http://example.com/live/105.ts", "live"),
            ]);

        var service = new HdHomeRunLineupService();
        var context = new DefaultHttpContext();

        var result = await service.TryBuildActiveLineupAsync("http://test-host:8080", lineup, context, CancellationToken.None);

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(
            new[] { "100", "101", "102", "103", "104", "105", "129", "130", "131" },
            result.Channels.Select(c => c.GuideNumber).ToArray());
    }

    [TestMethod]
    public void TryResolveStreamKeyByGuideNumber_MatchesLiveGuideNumbers()
    {
        var lineup = new RenderedLineup(
            SnapshotId: "snapshot-1",
            ProfileId: "profile-1",
            SnapshotCreatedUtc: DateTime.UtcNow,
            ChannelIndexPath: "unused",
            XmltvPath: null,
            Channels:
            [
                new RenderedLineupChannel("live-1", "Alpha", "alpha.tv", "Alpha", null, "News", 101, "http://example.com/live/alpha.ts", "live"),
                new RenderedLineupChannel("live-2", "Bravo", "bravo.tv", "Bravo", null, "News", null, "http://example.com/live/bravo.ts", "live"),
                new RenderedLineupChannel("vod-1", "Movie One", "movie.one", null, null, "Movies", null, "http://example.com/movie/one.mkv", "vod"),
            ]);

        var service = new HdHomeRunLineupService();

        var explicitGuide = service.TryResolveStreamKeyByGuideNumber(lineup, "101", CancellationToken.None);
        var fallbackGuide = service.TryResolveStreamKeyByGuideNumber(lineup, "1000", CancellationToken.None);

        Assert.AreEqual("live-1", explicitGuide);
        Assert.AreEqual("live-2", fallbackGuide);
    }

    [TestMethod]
    public void TryResolveStreamKeyByGuideNumber_UnknownGuideNumber_ReturnsNull()
    {
        var lineup = new RenderedLineup(
            SnapshotId: "snapshot-1",
            ProfileId: "profile-1",
            SnapshotCreatedUtc: DateTime.UtcNow,
            ChannelIndexPath: "unused",
            XmltvPath: null,
            Channels:
            [
                new RenderedLineupChannel("live-1", "Alpha", "alpha.tv", "Alpha", null, "News", 101, "http://example.com/live/alpha.ts", "live"),
            ]);

        var service = new HdHomeRunLineupService();

        var result = service.TryResolveStreamKeyByGuideNumber(lineup, "999", CancellationToken.None);

        Assert.IsNull(result);
    }
}
