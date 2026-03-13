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
        Assert.AreEqual("http://test-host:8080/hdhr/tune/live-1", first.Url);

        Assert.AreEqual("live-2", second.ChannelId);
        Assert.AreEqual("1000", second.GuideNumber);
        Assert.AreEqual("Bravo Name", second.GuideName);
        Assert.AreEqual("http://test-host:8080/hdhr/tune/live-2", second.Url);
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
}
