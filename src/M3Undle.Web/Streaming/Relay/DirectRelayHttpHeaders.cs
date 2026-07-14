using Microsoft.Extensions.Primitives;

namespace M3Undle.Web.Streaming.Relay;

internal static class DirectRelayHttpHeaders
{
    public static void ApplyRequestHeaders(HttpRequest request, HttpRequestMessage upstreamRequest)
    {
        CopyRequestHeader(request, upstreamRequest, "Range");
        CopyRequestHeader(request, upstreamRequest, "If-Range");
        CopyRequestHeader(request, upstreamRequest, "If-Match");
        CopyRequestHeader(request, upstreamRequest, "If-None-Match");
        CopyRequestHeader(request, upstreamRequest, "If-Modified-Since");
        CopyRequestHeader(request, upstreamRequest, "If-Unmodified-Since");
    }

    public static void ApplyResponseHeaders(HttpResponse response, HttpResponseMessage upstreamResponse)
    {
        response.StatusCode = (int)upstreamResponse.StatusCode;

        if (upstreamResponse.Content.Headers.ContentType is not null)
            response.ContentType = upstreamResponse.Content.Headers.ContentType.ToString();

        if (upstreamResponse.Content.Headers.ContentLength.HasValue)
            response.ContentLength = upstreamResponse.Content.Headers.ContentLength.Value;

        CopyResponseHeader(upstreamResponse.Headers, response, "Accept-Ranges");
        CopyResponseHeader(upstreamResponse.Content.Headers, response, "Content-Range");
        CopyResponseHeader(upstreamResponse.Headers, response, "ETag");
        CopyResponseHeader(upstreamResponse.Content.Headers, response, "Last-Modified");
    }

    private static void CopyRequestHeader(HttpRequest request, HttpRequestMessage upstreamRequest, string name)
    {
        if (request.Headers.TryGetValue(name, out var values))
            upstreamRequest.Headers.TryAddWithoutValidation(name, values.ToArray());
    }

    private static void CopyResponseHeader(
        System.Net.Http.Headers.HttpHeaders source,
        HttpResponse response,
        string name)
    {
        if (source.TryGetValues(name, out var values))
            response.Headers[name] = new StringValues(values.ToArray());
    }
}
