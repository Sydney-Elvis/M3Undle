using M3Undle.Web.Application;
using M3Undle.Web.Application.Epg;
using M3Undle.Web.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Text;

namespace M3Undle.Web.Tests.Epg;

/// <summary>
/// Checklist: Multiple EPG sources can be fetched and parsed successfully.
/// Covers xmltv_url (HTTP ok, 304 not-modified, failure → cache fallback) and xmltv_file kinds.
/// </summary>
[TestClass]
public sealed class EpgSourceFetcherTests
{
    private static readonly string SampleXmltv = """
        <?xml version="1.0" encoding="utf-8"?>
        <tv>
          <channel id="cnn.us"><display-name>CNN</display-name></channel>
          <programme start="20260315120000 +0000" stop="20260315130000 +0000" channel="cnn.us">
            <title>Breaking News</title>
          </programme>
        </tv>
        """;

    // -------------------------------------------------------------------------
    // xmltv_url (HTTP)
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task FetchAsync_HttpUrl_200Ok_ReturnsOkResultWithXml()
    {
        var handler = new FakeHttpHandler(_ =>
            Task.FromResult(OkResponse(SampleXmltv)));
        var fetcher = CreateFetcher(handler);
        var source = MakeHttpSource("src-1");

        var (result, xml) = await fetcher.FetchAsync(source, null, TempCachePath(), CancellationToken.None);

        Assert.AreEqual("ok", result.Status);
        Assert.IsNotNull(xml);
        StringAssert.Contains(xml, "Breaking News");
        Assert.IsGreaterThan(0L, result.Bytes);
    }

    [TestMethod]
    public async Task FetchAsync_HttpUrl_304NotModified_ReturnsNotModifiedStatus()
    {
        var handler = new FakeHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)));
        var fetcher = CreateFetcher(handler);
        var source = MakeHttpSource("src-2", etag: "\"abc123\"");

        var (result, _) = await fetcher.FetchAsync(source, null, TempCachePath(), CancellationToken.None);

        Assert.AreEqual("not_modified", result.Status);
        Assert.IsNull(result.Xml);
    }

    [TestMethod]
    public async Task FetchAsync_HttpUrl_ServerError_ReturnsFailStatus()
    {
        var handler = new FakeHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var fetcher = CreateFetcher(handler);
        var source = MakeHttpSource("src-3");

        var (result, xml) = await fetcher.FetchAsync(source, null, TempCachePath(), CancellationToken.None);

        Assert.AreEqual("fail", result.Status);
        Assert.IsNull(xml);
        Assert.IsNotNull(result.ErrorSummary);
    }

    [TestMethod]
    public async Task FetchAsync_HttpUrl_ServerError_FallsBackToCacheFile()
    {
        var cachePath = TempCachePath();
        await File.WriteAllTextAsync(cachePath, SampleXmltv, Encoding.UTF8);

        var handler = new FakeHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var fetcher = CreateFetcher(handler);
        var source = MakeHttpSource("src-4");

        var (result, xml) = await fetcher.FetchAsync(source, null, cachePath, CancellationToken.None);

        Assert.AreEqual("fail", result.Status);
        Assert.IsNotNull(xml, "Should have fallen back to cached XML on failure");
        StringAssert.Contains(xml, "Breaking News");

        File.Delete(cachePath);
    }

    [TestMethod]
    public async Task FetchAsync_HttpUrl_ETagRoundtrip_ReturnedETagIsPreserved()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var response = OkResponse(SampleXmltv);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"xyz789\"");
            return Task.FromResult(response);
        });
        var fetcher = CreateFetcher(handler);
        var source = MakeHttpSource("src-5");

        var (result, _) = await fetcher.FetchAsync(source, null, TempCachePath(), CancellationToken.None);

        Assert.AreEqual("ok", result.Status);
        Assert.AreEqual("\"xyz789\"", result.ETag);
    }

    // -------------------------------------------------------------------------
    // xmltv_file
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task FetchAsync_FileSource_ExistingFile_ReturnsOkWithContent()
    {
        var path = TempCachePath();
        await File.WriteAllTextAsync(path, SampleXmltv, Encoding.UTF8);

        var fetcher = CreateFetcher(new FakeHttpHandler(_ => throw new InvalidOperationException("should not be called")));
        var source = new EpgSource
        {
            EpgSourceId = "file-src-1",
            Name = "Local EPG",
            Kind = "xmltv_file",
            UrlOrPath = path,
            Enabled = true,
            TimeoutSeconds = 30,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };

        var (result, xml) = await fetcher.FetchAsync(source, null, TempCachePath(), CancellationToken.None);

        Assert.AreEqual("ok", result.Status);
        Assert.IsNotNull(xml);
        StringAssert.Contains(xml, "Breaking News");

        File.Delete(path);
    }

    [TestMethod]
    public async Task FetchAsync_FileSource_MissingFile_ReturnsFailStatus()
    {
        var fetcher = CreateFetcher(new FakeHttpHandler(_ => throw new InvalidOperationException("should not be called")));
        var source = new EpgSource
        {
            EpgSourceId = "file-src-2",
            Name = "Missing File",
            Kind = "xmltv_file",
            UrlOrPath = "/nonexistent/path/guide.xml",
            Enabled = true,
            TimeoutSeconds = 30,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };

        var (result, xml) = await fetcher.FetchAsync(source, null, TempCachePath(), CancellationToken.None);

        Assert.AreEqual("fail", result.Status);
        Assert.IsNull(xml);
    }

    // -------------------------------------------------------------------------
    // Multiple sources (checklist item)
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task FetchAsync_MultipleSources_BothCanBeFetchedAndParsed()
    {
        // Checklist: Multiple EPG sources can be fetched and parsed successfully.
        // Two independent xmltv_url sources — both return valid XML.
        var responseA = SampleXmltv.Replace("cnn.us", "source-a.ch").Replace("Breaking News", "Source A Show");
        var responseB = SampleXmltv.Replace("cnn.us", "source-b.ch").Replace("Breaking News", "Source B Show");

        var callIndex = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            var body = Interlocked.Increment(ref callIndex) == 1 ? responseA : responseB;
            return Task.FromResult(OkResponse(body));
        });

        var fetcher = CreateFetcher(handler);
        var parser = new XmltvParser();

        var sourceA = MakeHttpSource("epg-source-a");
        var sourceB = MakeHttpSource("epg-source-b");

        var (resultA, xmlA) = await fetcher.FetchAsync(sourceA, null, TempCachePath(), CancellationToken.None);
        var (resultB, xmlB) = await fetcher.FetchAsync(sourceB, null, TempCachePath(), CancellationToken.None);

        Assert.AreEqual("ok", resultA.Status);
        Assert.AreEqual("ok", resultB.Status);
        Assert.IsNotNull(xmlA);
        Assert.IsNotNull(xmlB);

        var catalogueA = parser.Parse(sourceA.EpgSourceId, xmlA);
        var catalogueB = parser.Parse(sourceB.EpgSourceId, xmlB);

        Assert.IsTrue(catalogueA.Channels.Any(c => c.XmltvChannelId == "source-a.ch"));
        Assert.IsTrue(catalogueB.Channels.Any(c => c.XmltvChannelId == "source-b.ch"));

        var programA = catalogueA.ProgrammesByChannel["source-a.ch"].Single();
        var programB = catalogueB.ProgrammesByChannel["source-b.ch"].Single();

        Assert.AreEqual("Source A Show", programA.Title);
        Assert.AreEqual("Source B Show", programB.Title);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static EpgSourceFetcher CreateFetcher(HttpMessageHandler handler)
    {
        var envVarService = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        return new EpgSourceFetcher(
            new FakeHttpClientFactory(handler),
            null!,  // ProviderFetcher — not used by xmltv_url or xmltv_file kinds
            envVarService,
            NullLogger<EpgSourceFetcher>.Instance);
    }

    private static EpgSource MakeHttpSource(string id, string? etag = null) => new()
    {
        EpgSourceId = id,
        Name = id,
        Kind = "xmltv_url",
        UrlOrPath = "http://fake-epg-server/guide.xml",
        Priority = 1,
        Enabled = true,
        TimeoutSeconds = 30,
        ETag = etag,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static string TempCachePath()
        => Path.Combine(Path.GetTempPath(), $"epg-test-{Guid.NewGuid():N}.xml");

    private static HttpResponseMessage OkResponse(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        return response;
    }

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request);
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
