using M3Undle.Web.Streaming.Compatibility;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class PlaybackModeResolverTests
{
    [TestMethod]
    public void RequiresHls_ForceTs_AlwaysFalse()
    {
        var context = CreateContext("/live/key", query: "?format=hls", userAgent: "Mozilla/5.0");

        var result = PlaybackModeResolver.RequiresHls(context, forceTs: true);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void RequiresHls_QueryFormatHls_True()
    {
        var context = CreateContext("/live/key", query: "?format=hls", userAgent: "VLC/3.0");

        var result = PlaybackModeResolver.RequiresHls(context, forceTs: false);

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void RequiresHls_HlsTrueQuery_IsIgnoredInV1()
    {
        var context = CreateContext("/live/key", query: "?hls=true", userAgent: "VLC/3.0");

        var result = PlaybackModeResolver.RequiresHls(context, forceTs: false);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void RequiresHls_BrowserFallback_True()
    {
        var context = CreateContext("/live/key", query: null, userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        var result = PlaybackModeResolver.RequiresHls(context, forceTs: false);

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void RequiresHls_NativeClientWithoutExplicitSignal_False()
    {
        var context = CreateContext("/live/key", query: null, userAgent: "VLC/3.0");

        var result = PlaybackModeResolver.RequiresHls(context, forceTs: false);

        Assert.IsFalse(result);
    }

    // Electron apps (e.g. iptvnator) use mpegts.js/MSE to play MPEG-TS directly.
    // Redirecting them to generated HLS causes mpegts.js to receive M3U8 text via XHR
    // and fail. Electron UAs always include "Electron/" alongside "Mozilla/".

    [TestMethod]
    public void IsBrowserClient_ElectronOnlyUa_False()
    {
        var context = CreateContext("/live/key", query: null, userAgent: "Electron/28.1.0");

        Assert.IsFalse(PlaybackModeResolver.IsBrowserClient(context));
    }

    [TestMethod]
    public void IsBrowserClient_ElectronCompositeUa_False()
    {
        const string electronUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) iptvnator/0.9.0 Chrome/120.0.0.0 Electron/28.1.0 Safari/537.36";
        var context = CreateContext("/live/key", query: null, userAgent: electronUa);

        Assert.IsFalse(PlaybackModeResolver.IsBrowserClient(context));
    }

    [TestMethod]
    public void RequiresHls_ElectronCompositeUa_False()
    {
        const string electronUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) iptvnator/0.9.0 Chrome/120.0.0.0 Electron/28.1.0 Safari/537.36";
        var context = CreateContext("/live/key", query: null, userAgent: electronUa);

        Assert.IsFalse(PlaybackModeResolver.RequiresHls(context, forceTs: false));
    }

    [TestMethod]
    public void RequiresHls_ElectronWithFormatHlsQuery_True()
    {
        const string electronUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) iptvnator/0.9.0 Chrome/120.0.0.0 Electron/28.1.0 Safari/537.36";
        var context = CreateContext("/live/key", query: "?format=hls", userAgent: electronUa);

        // Explicit ?format=hls still forces HLS even for Electron clients.
        Assert.IsTrue(PlaybackModeResolver.RequiresHls(context, forceTs: false));
    }

    [TestMethod]
    [DataRow("Roku/DVP-13.0 (13.0.0.4189-00)")]
    [DataRow("smarters pro/1.0 (Linux;Android 12) SmartersPro")]
    [DataRow("ExoPlayerLib/2.19.1 (Linux;Android 12) ExoPlayerLib/2.19.1")]
    [DataRow("Media3ExoPlayer/1.3.1 (Linux;Android 13)")]
    [DataRow("Dalvik/2.1.0 (Linux; U; Android 12; Hisense Build/STT1.211007.001)")]
    [DataRow("TiviMate/4.7.0 (okhttp/4.12.0)")]
    [DataRow("okhttp/4.12.0")]
    public void IsBurstBufferingUserAgent_KnownBurstBufferers_True(string userAgent)
        => Assert.IsTrue(PlaybackModeResolver.IsBurstBufferingUserAgent(userAgent));

    [TestMethod]
    [DataRow("VLC/3.0.20 LibVLC/3.0.20")]
    [DataRow("Lavf/60.16.100")]
    [DataRow("Mozilla/5.0 (Windows NT 10.0; Win64; x64)")]
    [DataRow("")]
    [DataRow(null)]
    public void IsBurstBufferingUserAgent_ContinuousReaders_False(string? userAgent)
        => Assert.IsFalse(PlaybackModeResolver.IsBurstBufferingUserAgent(userAgent));

    private static DefaultHttpContext CreateContext(string path, string? query, string userAgent)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = query is null ? QueryString.Empty : new QueryString(query);
        context.Request.Headers.UserAgent = userAgent;
        return context;
    }
}
