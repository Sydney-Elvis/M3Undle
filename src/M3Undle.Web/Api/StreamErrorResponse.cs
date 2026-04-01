using Microsoft.AspNetCore.Http;

namespace M3Undle.Web.Api;

internal static class StreamErrorResponse
{
    internal static async Task WriteHtmlErrorAsync(
        HttpResponse response,
        int statusCode,
        string message,
        CancellationToken cancellationToken = default)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-store";

        var html = $$$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Stream Unavailable</title>
            <style>
            body{font-family:system-ui,sans-serif;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0;background:#111;color:#e0e0e0}
            .card{text-align:center;max-width:480px;padding:2rem}
            h1{font-size:1.4rem;margin:0 0 .5rem}
            p{color:#999;margin:0}
            </style>
            </head>
            <body><div class="card"><h1>Stream Unavailable</h1><p>{{{HtmlEncode(message)}}}</p></div></body>
            </html>
            """;

        await response.WriteAsync(html, cancellationToken);
    }

    private static string HtmlEncode(string value)
        => System.Net.WebUtility.HtmlEncode(value);
}
