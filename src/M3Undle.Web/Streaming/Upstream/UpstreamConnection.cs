using System.Net.Http;

namespace M3Undle.Web.Streaming.Upstream;

public sealed class UpstreamConnection(HttpClient client, HttpResponseMessage response, Stream stream) : IAsyncDisposable
{
    public HttpClient Client { get; } = client;

    public HttpResponseMessage Response { get; } = response;

    public Stream Stream { get; } = stream;

    public string? ContentType => Response.Content.Headers.ContentType?.ToString();

    public int StatusCode => (int)Response.StatusCode;

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        Response.Dispose();
        Client.Dispose();
    }
}

