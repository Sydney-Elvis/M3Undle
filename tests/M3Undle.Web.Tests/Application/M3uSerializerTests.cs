using M3Undle.Web.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class M3uSerializerTests
{
    private readonly M3uSerializer _serializer = new();

    [TestMethod]
    public async Task WriteAsync_LiveNumericTail_EmitsTsTail()
    {
        var output = await SerializeSingleChannelAsync(new RenderedLineupChannel(
            StreamKey: "live-key",
            DisplayName: "Live One",
            TvgId: null,
            TvgName: null,
            LogoUrl: null,
            GroupTitle: "News",
            TvgChno: 101,
            StreamUrl: "http://provider.test/live/user/pass/20312",
            ContentType: "live"));

        StringAssert.Contains(output, "http://proxy.test/live/live-key/20312.ts");
    }

    [TestMethod]
    public async Task WriteAsync_LiveM3u8Tail_DoesNotForceTsTail()
    {
        var output = await SerializeSingleChannelAsync(new RenderedLineupChannel(
            StreamKey: "live-key",
            DisplayName: "Live HLS",
            TvgId: null,
            TvgName: null,
            LogoUrl: null,
            GroupTitle: "News",
            TvgChno: 101,
            StreamUrl: "http://provider.test/live/user/pass/20312.m3u8",
            ContentType: "live"));

        StringAssert.Contains(output, "http://proxy.test/live/live-key/20312.m3u8");
        Assert.IsFalse(output.Contains("/20312.ts", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WriteAsync_Vod_UsesMovieRouteAndUpstreamTail()
    {
        var output = await SerializeSingleChannelAsync(new RenderedLineupChannel(
            StreamKey: "vod-key",
            DisplayName: "Movie One",
            TvgId: null,
            TvgName: null,
            LogoUrl: null,
            GroupTitle: "Movies",
            TvgChno: null,
            StreamUrl: "http://provider.test/movie/user/pass/5001.mp4",
            ContentType: "vod"));

        StringAssert.Contains(output, "http://proxy.test/movie/vod-key/5001.mp4");
    }

    [TestMethod]
    public async Task WriteAsync_LiveHlsQuery_DoesNotForceTsTail()
    {
        var output = await SerializeSingleChannelAsync(new RenderedLineupChannel(
            StreamKey: "live-key",
            DisplayName: "Live HLS Query",
            TvgId: null,
            TvgName: null,
            LogoUrl: null,
            GroupTitle: "News",
            TvgChno: 101,
            StreamUrl: "http://provider.test/live/user/pass/20312?output=m3u8",
            ContentType: "live"));

        StringAssert.Contains(output, "http://proxy.test/live/live-key/20312");
        Assert.IsFalse(output.Contains("/20312.ts", StringComparison.Ordinal));
    }

    private async Task<string> SerializeSingleChannelAsync(RenderedLineupChannel channel)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("proxy.test");
        context.Response.Body = new MemoryStream();

        var lineup = new RenderedLineup(
            SnapshotId: "snap-1",
            ProfileId: "profile-1",
            SnapshotCreatedUtc: DateTime.UtcNow,
            ChannelIndexPath: "/tmp/channel_index.idx",
            XmltvPath: "/tmp/m3undle.xml",
            Channels: [channel]);

        await _serializer.WriteAsync(context, lineup, CancellationToken.None);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }
}
