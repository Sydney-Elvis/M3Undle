using System.Net;
using System.Net.Http.Headers;
using M3Undle.Web.Streaming.Relay;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class DirectRelayHttpHeadersTests
{
    [TestMethod]
    public void ApplyRequestHeaders_ForwardsRangeAndConditionalHeaders()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Range = "bytes=100-199";
        context.Request.Headers.IfRange = "\"version-1\"";
        context.Request.Headers.IfMatch = "\"version-1\"";
        context.Request.Headers.IfNoneMatch = "\"version-0\"";
        context.Request.Headers.IfModifiedSince = "Sun, 12 Jul 2026 00:00:00 GMT";
        context.Request.Headers.IfUnmodifiedSince = "Mon, 13 Jul 2026 12:00:00 GMT";
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Get, "http://provider.test/movie.mkv");

        DirectRelayHttpHeaders.ApplyRequestHeaders(context.Request, upstreamRequest);

        Assert.AreEqual("bytes=100-199", upstreamRequest.Headers.Range?.ToString());
        Assert.AreEqual("\"version-1\"", upstreamRequest.Headers.IfRange?.ToString());
        Assert.AreEqual("\"version-1\"", upstreamRequest.Headers.IfMatch.ToString());
        Assert.AreEqual("\"version-0\"", upstreamRequest.Headers.IfNoneMatch.ToString());
        Assert.AreEqual("Sun, 12 Jul 2026 00:00:00 GMT", upstreamRequest.Headers.IfModifiedSince?.ToString("r"));
        Assert.AreEqual("Mon, 13 Jul 2026 12:00:00 GMT", upstreamRequest.Headers.IfUnmodifiedSince?.ToString("r"));
    }

    [TestMethod]
    public void ApplyResponseHeaders_PreservesPartialContentSemantics()
    {
        var context = new DefaultHttpContext();
        using var upstreamResponse = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(new byte[100]),
        };
        upstreamResponse.Headers.AcceptRanges.Add("bytes");
        upstreamResponse.Headers.ETag = new EntityTagHeaderValue("\"version-1\"");
        upstreamResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("video/x-matroska");
        upstreamResponse.Content.Headers.ContentRange = new ContentRangeHeaderValue(100, 199, 1000);
        upstreamResponse.Content.Headers.LastModified = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

        DirectRelayHttpHeaders.ApplyResponseHeaders(context.Response, upstreamResponse);

        Assert.AreEqual(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.AreEqual(100, context.Response.ContentLength);
        Assert.AreEqual("video/x-matroska", context.Response.ContentType);
        Assert.AreEqual("bytes", context.Response.Headers.AcceptRanges.ToString());
        Assert.AreEqual("bytes 100-199/1000", context.Response.Headers.ContentRange.ToString());
        Assert.AreEqual("\"version-1\"", context.Response.Headers.ETag.ToString());
        Assert.AreEqual("Mon, 13 Jul 2026 12:00:00 GMT", context.Response.Headers.LastModified.ToString());
    }
}
