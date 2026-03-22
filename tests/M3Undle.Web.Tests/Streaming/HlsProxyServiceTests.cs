using System.Net;
using M3Undle.Web.Streaming.Compatibility;
using M3Undle.Web.Streaming.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class HlsProxyServiceTests
{
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
