using System.Net;
using System.Text;
using M3Undle.Web.Streaming.Compatibility;
using M3Undle.Web.Streaming.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class HlsProxyServiceTests
{
    [TestMethod]
    public async Task FetchAndRewriteManifestAsync_UsesRedirectedHostForRelativeSegments_WhenManifestUrlRedirects()
    {
        // Regression: when the upstream manifest URL redirects to a different host, relative segment
        // paths must be resolved against the *final* (redirected) URI, not the original request URL.
        // Before the fix, relative paths were resolved against the original host, producing 403s.
        var handler = new RedirectSimulatingResponseHandler(
            finalUri: new Uri("https://bingbongvpn.test/live/play/channel"),
            body: """
                #EXTM3U
                #EXTINF:-1,
                /hls/abc123/seg001.ts
                """);

        var service = new HlsProxyService(
            new FakeHttpClientFactory(handler),
            new FailIfUsedServiceScopeFactory(),
            new HlsManifestRewriter(),
            NullLogger<HlsProxyService>.Instance);

        var descriptor = new StreamSourceDescriptor(
            ProfileId: "profile-1",
            ProviderId: string.Empty,
            ProviderChannelId: "provider-channel-1",
            StreamUrl: "https://pinkponyclub.test/live/channel.m3u8",
            DisplayName: "Test Channel",
            RequestedRoute: "/live/test",
            UserAgent: null,
            RemoteIp: null);

        var rewritten = await service.FetchAndRewriteManifestAsync(
            ["https://pinkponyclub.test/live/channel.m3u8"],
            descriptor,
            "https://proxy.test/hls/key/proxy",
            CancellationToken.None);

        Assert.IsNotNull(rewritten);
        // Segment must be resolved against bingbongvpn.test (the redirect target), not pinkponyclub.test
        StringAssert.Contains(rewritten, WebEncoders.Base64UrlEncode(
            System.Text.Encoding.UTF8.GetBytes("https://bingbongvpn.test/hls/abc123/seg001.ts")));
        Assert.IsFalse(rewritten.Contains("pinkponyclub.test", StringComparison.Ordinal),
            "Segment URL must not reference the original (pre-redirect) host.");
    }

    [TestMethod]
    public async Task FetchAndRewriteManifestAsync_PreservesExistingQuery_WhenAddingUParameter()
    {
        var handler = new StaticResponseHandler("""
            #EXTM3U
            segment.ts
            """);

        var service = new HlsProxyService(
            new FakeHttpClientFactory(handler),
            new FailIfUsedServiceScopeFactory(),
            new HlsManifestRewriter(),
            NullLogger<HlsProxyService>.Instance);

        var descriptor = new StreamSourceDescriptor(
            ProfileId: "profile-1",
            ProviderId: string.Empty,
            ProviderChannelId: "provider-channel-1",
            StreamUrl: "https://upstream.test/master.m3u8",
            DisplayName: "Test Channel",
            RequestedRoute: "/live/test",
            UserAgent: null,
            RemoteIp: null);

        var rewritten = await service.FetchAndRewriteManifestAsync(
            ["https://upstream.test/master.m3u8"],
            descriptor,
            "https://proxy.test/hls/key/proxy?username=user&password=pass",
            CancellationToken.None);

        Assert.IsNotNull(rewritten);
        StringAssert.Contains(rewritten, "https://proxy.test/hls/key/proxy?username=user&password=pass&u=");
        Assert.IsFalse(rewritten.Contains("?username=user&password=pass?u=", StringComparison.Ordinal));
    }

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
                RequestMessage = request,
            });
    }

    /// <summary>
    /// Simulates an HTTP redirect by returning a successful response whose
    /// <see cref="HttpResponseMessage.RequestMessage"/> points to <paramref name="finalUri"/>
    /// rather than the original request URI — matching what <see cref="HttpClientHandler"/>
    /// produces after following redirects.
    /// </summary>
    private sealed class RedirectSimulatingResponseHandler(Uri finalUri, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri),
            });
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FailIfUsedServiceScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new AssertFailedException("Provider metadata lookup should not be called in this test.");
    }
}
