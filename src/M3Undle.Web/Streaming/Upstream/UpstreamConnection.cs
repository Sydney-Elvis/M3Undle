using System.Diagnostics;
using System.Net.Http;

namespace M3Undle.Web.Streaming.Upstream;

public sealed class UpstreamConnection : IAsyncDisposable
{
    private readonly HttpClient? _client;
    private readonly HttpResponseMessage? _response;
    private readonly Process? _process;
    private readonly string? _contentType;
    private readonly int _statusCode;

    public UpstreamConnection(HttpClient client, HttpResponseMessage response, Stream stream)
    {
        _client = client;
        _response = response;
        Stream = stream;
        _statusCode = (int)response.StatusCode;
        _contentType = response.Content.Headers.ContentType?.ToString();
    }

    public UpstreamConnection(Process process, Stream stream, string contentType, int statusCode = 200)
    {
        _process = process;
        Stream = stream;
        _contentType = contentType;
        _statusCode = statusCode;
    }

    public HttpResponseMessage? Response => _response;

    public Stream Stream { get; }

    public string? ContentType => _contentType;

    public int StatusCode => _statusCode;

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        _response?.Dispose();
        _client?.Dispose();
        if (_process is not null)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            try { await _process.WaitForExitAsync(); } catch { }
            _process.Dispose();
        }
    }
}
