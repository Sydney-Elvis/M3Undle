using System.Net;
using M3Undle.Web.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class HttpFetchHelperTests
{
    [TestMethod]
    public async Task FetchStringAsync_FollowsHttpsToHttpRedirect()
    {
        // .NET's built-in redirect handling refuses this downgrade; providers do it for XMLTV.
        using var client = new HttpClient(new StubHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://provider.example/xmltv.php" => Redirect("http://cdn.example/epg.xml"),
            "http://cdn.example/epg.xml" => Ok("<tv/>"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        var body = await HttpFetchHelper.FetchStringAsync(client, "https://provider.example/xmltv.php", 30, CancellationToken.None);

        Assert.AreEqual("<tv/>", body);
    }

    [TestMethod]
    public async Task FetchStringAsync_ResolvesRelativeRedirectLocation()
    {
        using var client = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/old/xmltv.php" => Redirect("/new/epg.xml"),
            "/new/epg.xml" => Ok("<tv/>"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        var body = await HttpFetchHelper.FetchStringAsync(client, "http://provider.example/old/xmltv.php", 30, CancellationToken.None);

        Assert.AreEqual("<tv/>", body);
    }

    [TestMethod]
    public async Task FetchStringAsync_RedirectLoopFailsWithClearError()
    {
        using var client = new HttpClient(new StubHandler(_ => Redirect("http://loop.example/a")));

        var ex = await Assert.ThrowsExactlyAsync<ProviderFetchException>(() =>
            HttpFetchHelper.FetchStringAsync(client, "http://loop.example/a", 30, CancellationToken.None));

        StringAssert.Contains(ex.Message, "Too many redirects");
        StringAssert.Contains(ex.Message, "loop.example");
    }

    [TestMethod]
    public async Task FetchStringAsync_RedirectWithoutLocationStillFails()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)));

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            HttpFetchHelper.FetchStringAsync(client, "http://provider.example/x", 30, CancellationToken.None));
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
