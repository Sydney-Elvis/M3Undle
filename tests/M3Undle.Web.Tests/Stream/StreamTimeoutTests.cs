using M3Undle.Core.M3u;
using M3Undle.Web.Application;
using M3Undle.Web.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;

namespace M3Undle.Web.Tests.Stream;

[TestClass]
public sealed class StreamTimeoutTests
{
    // -------------------------------------------------------------------------
    // FetchPlaylistAsync — error handling
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task FetchPlaylist_HttpError_ThrowsProviderFetchException()
    {
        var fetcher = CreateFetcher(new ErrorHttpMessageHandler(HttpStatusCode.InternalServerError));

        await AssertThrowsAsync<ProviderFetchException>(
            () => fetcher.FetchPlaylistAsync(SimpleProvider(), CancellationToken.None));
    }

    [TestMethod]
    public async Task FetchPlaylist_Timeout_ThrowsProviderFetchException()
    {
        var fetcher = CreateFetcher(new TimeoutHttpMessageHandler());

        await AssertThrowsAsync<ProviderFetchException>(
            () => fetcher.FetchPlaylistAsync(SimpleProvider(), CancellationToken.None));
    }

    [TestMethod]
    public async Task FetchPlaylist_Success_ReturnsChannels()
    {
        var m3u =
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-id=\"cnn.us\" tvg-name=\"CNN\" group-title=\"News\",CNN US\n" +
            "http://example.com/stream/cnn\n" +
            "#EXTINF:-1 tvg-id=\"espn.hd\" group-title=\"Sports\",ESPN HD\n" +
            "http://example.com/stream/espn\n";

        var fetcher = CreateFetcher(new OkHttpMessageHandler(m3u));

        var result = await fetcher.FetchPlaylistAsync(SimpleProvider(), CancellationToken.None);

        Assert.HasCount(2, result.Channels);
        Assert.IsGreaterThan(0, result.Bytes);

        var cnn = result.Channels.Single(x => x.ProviderChannelKey == "cnn.us");
        Assert.AreEqual("CNN US", cnn.DisplayName);
        Assert.AreEqual("News", cnn.GroupTitle);
    }

    [TestMethod]
    public async Task FetchPlaylist_NoValidEntries_ReturnsEmptyChannels()
    {
        var fetcher = CreateFetcher(new OkHttpMessageHandler("#EXTM3U\n# no channels here\n"));

        var result = await fetcher.FetchPlaylistAsync(SimpleProvider(), CancellationToken.None);

        Assert.IsEmpty(result.Channels);
    }

    [TestMethod]
    public async Task FetchPlaylist_EntryWithNoUrl_IsExcluded()
    {
        var m3u = "#EXTM3U\n#EXTINF:-1,Channel Without URL\n\n";
        var fetcher = CreateFetcher(new OkHttpMessageHandler(m3u));

        var result = await fetcher.FetchPlaylistAsync(SimpleProvider(), CancellationToken.None);

        Assert.IsEmpty(result.Channels);
    }

    // -------------------------------------------------------------------------
    // FetchXmltvAsync — error handling
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task FetchXmltvAsync_NoXmltvUrl_ReturnsEmptyDocument()
    {
        var fetcher = CreateFetcher(new OkHttpMessageHandler(""));
        var provider = new Provider
        {
            ProviderId = "p1",
            Name = "test",
            Enabled = true,
            PlaylistUrl = "http://example.com/playlist.m3u",
            XmltvUrl = null,
            TimeoutSeconds = 20,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };

        var result = await fetcher.FetchXmltvAsync(provider, CancellationToken.None);

        Assert.AreEqual(0, result.Bytes);
        Assert.Contains("<tv", result.Xml);
    }

    [TestMethod]
    public async Task FetchXmltvAsync_HttpError_ThrowsProviderFetchException()
    {
        var fetcher = CreateFetcher(new ErrorHttpMessageHandler(HttpStatusCode.ServiceUnavailable));

        await AssertThrowsAsync<ProviderFetchException>(
            () => fetcher.FetchXmltvAsync(SimpleProviderWithXmltvUrl(), CancellationToken.None));
    }

    [TestMethod]
    public async Task FetchXmltvAsync_Success_ReturnsBytesAndContent()
    {
        var xml = "<?xml version=\"1.0\"?><tv></tv>";
        var fetcher = CreateFetcher(new OkHttpMessageHandler(xml));

        var result = await fetcher.FetchXmltvAsync(SimpleProviderWithXmltvUrl(), CancellationToken.None);

        Assert.IsGreaterThan(0, result.Bytes);
        Assert.AreEqual(xml, result.Xml);
    }

    // -------------------------------------------------------------------------
    // Test helpers
    // -------------------------------------------------------------------------

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            Assert.Fail($"Expected {typeof(TException).Name} to be thrown.");
        }
        catch (TException)
        {
            // Expected
        }
    }

    private static ProviderFetcher CreateFetcher(HttpMessageHandler handler)
    {
        var factory = new FakeHttpClientFactory(handler);
        var envSvc = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        return new ProviderFetcher(factory, new PlaylistParser(), envSvc, NullLogger<ProviderFetcher>.Instance);
    }

    private static Provider SimpleProvider() => new()
    {
        ProviderId = "p1",
        Name = "test",
        Enabled = true,
        PlaylistUrl = "http://example.com/playlist.m3u",
        XmltvUrl = null,
        TimeoutSeconds = 20,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static Provider SimpleProviderWithXmltvUrl() => new()
    {
        ProviderId = "p1",
        Name = "test",
        Enabled = true,
        PlaylistUrl = "http://example.com/playlist.m3u",
        XmltvUrl = "http://example.com/xmltv.xml",
        TimeoutSeconds = 20,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class OkHttpMessageHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
            });
        }
    }

    private sealed class ErrorHttpMessageHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class TimeoutHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout being exceeded.");
    }
}

