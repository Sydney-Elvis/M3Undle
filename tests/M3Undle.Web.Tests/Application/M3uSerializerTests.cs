using M3Undle.Web.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

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

    [TestMethod]
    public async Task WriteAsync_SetsContentLengthToUtf8ByteCount()
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
            Channels:
            [
                new RenderedLineupChannel(
                    StreamKey: "live-key",
                    DisplayName: "Live One",
                    TvgId: "chan-1",
                    TvgName: "Live One",
                    LogoUrl: "http://proxy.test/logo.png",
                    GroupTitle: "News",
                    TvgChno: 101,
                    StreamUrl: "http://provider.test/live/user/pass/20312",
                    ContentType: "live"),
            ]);

        await _serializer.WriteAsync(context, lineup, null, CancellationToken.None);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var output = await reader.ReadToEndAsync();

        Assert.AreEqual(Encoding.UTF8.GetByteCount(output), context.Response.ContentLength);
    }

    [TestMethod]
    public async Task WriteAsync_NullExpiry_DoesNotEmitExpiresAttribute()
    {
        var output = await SerializeSingleChannelAsync(
            new RenderedLineupChannel("k", "C", null, null, null, "G", null, "http://p.test/1", "live"),
            playlistExpiresUtc: null);

        var header = output.Split('\n')[0];
        Assert.IsFalse(header.Contains("x-playlist-expires", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WriteAsync_WithExpiry_EmitsXPlaylistExpiresAttributeOnHeaderLine()
    {
        var expiry = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var output = await SerializeSingleChannelAsync(
            new RenderedLineupChannel("k", "C", null, null, null, "G", null, "http://p.test/1", "live"),
            playlistExpiresUtc: expiry);

        var header = output.Split('\n')[0];
        StringAssert.Contains(header, "x-playlist-expires=\"2026-06-01T12:00:00Z\"");
    }

    private async Task<string> SerializeSingleChannelAsync(RenderedLineupChannel channel, DateTime? playlistExpiresUtc = null)
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

        await _serializer.WriteAsync(context, lineup, playlistExpiresUtc, CancellationToken.None);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }
}
