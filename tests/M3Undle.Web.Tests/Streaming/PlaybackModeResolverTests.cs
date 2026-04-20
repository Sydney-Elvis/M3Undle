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

    private static DefaultHttpContext CreateContext(string path, string? query, string userAgent)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = query is null ? QueryString.Empty : new QueryString(query);
        context.Request.Headers.UserAgent = userAgent;
        return context;
    }
}
