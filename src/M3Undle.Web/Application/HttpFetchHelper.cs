using System.Text;

namespace M3Undle.Web.Application;

// Streams an HTTP response body with an idle-activity timeout, plus a hard total-duration
// ceiling. The idle timer resets on every byte received, so a slow trickle (or a stalled
// connect/read whose cancellation the underlying socket doesn't actually honor promptly)
// could otherwise run indefinitely — the hard ceiling bounds the whole call regardless.
internal static class HttpFetchHelper
{
    // Floored at 5 minutes so small idle timeouts (e.g. 30s) don't produce an unreasonably
    // tight hard cap for a large response that's still making steady progress.
    private static readonly TimeSpan MinHardTimeout = TimeSpan.FromMinutes(5);

    private const int MaxRedirects = 5;

    /// <summary>
    /// GETs <paramref name="url"/>, following redirects manually. HttpClient's built-in redirect
    /// handling refuses HTTPS→HTTP hops (it surfaces the bare 302 as an error), but providers do
    /// redirect their XMLTV/playlist endpoints to plain-HTTP CDN hosts. The redirect target is
    /// fully specified by the provider, so following it adds no credential exposure beyond what
    /// the provider chose. Hops are capped to avoid redirect loops.
    /// </summary>
    internal static async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        var current = new Uri(url, UriKind.Absolute);
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
                return response;

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            response.Dispose();

            if (hop >= MaxRedirects)
                throw new ProviderFetchException($"Too many redirects (more than {MaxRedirects}); last target host: {next.Host}.");

            current = next;
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode status) => status is
        System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Found
        or System.Net.HttpStatusCode.SeeOther or System.Net.HttpStatusCode.TemporaryRedirect
        or System.Net.HttpStatusCode.PermanentRedirect;

    internal static async Task<string> FetchStringAsync(
        HttpClient client,
        string url,
        int idleTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var idleTimeout = TimeSpan.FromSeconds(idleTimeoutSeconds);
        var hardTimeout = TimeSpan.FromSeconds(idleTimeoutSeconds * 6);
        if (hardTimeout < MinHardTimeout)
            hardTimeout = MinHardTimeout;

        using var hardCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        hardCts.CancelAfter(hardTimeout);
        var hardToken = hardCts.Token;

        HttpResponseMessage response;
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(hardToken))
        {
            connectCts.CancelAfter(idleTimeout);
            try
            {
                response = await SendFollowingRedirectsAsync(client, url, connectCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProviderFetchException(hardToken.IsCancellationRequested
                    ? $"Fetch timed out after {hardTimeout.TotalSeconds:F0}s hard ceiling waiting for server response."
                    : $"Fetch timed out after {idleTimeoutSeconds}s waiting for server response.");
            }
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            await using var bodyStream = await response.Content.ReadAsStreamAsync(hardToken);
            using var ms = new MemoryStream();
            var buffer = new byte[65536];

            while (true)
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(hardToken);
                idleCts.CancelAfter(idleTimeout);

                int bytesRead;
                try
                {
                    bytesRead = await bodyStream.ReadAsync(buffer.AsMemory(), idleCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ProviderFetchException(hardToken.IsCancellationRequested
                        ? $"Fetch timed out after {hardTimeout.TotalSeconds:F0}s hard ceiling — no data received."
                        : $"Fetch timed out — no data received for {idleTimeoutSeconds}s.");
                }

                if (bytesRead == 0)
                    break;

                ms.Write(buffer, 0, bytesRead);
            }

            var encoding = Encoding.UTF8;
            if (response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
            {
                try { encoding = Encoding.GetEncoding(charset); }
                catch (ArgumentException) { }
            }

            return encoding.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
    }
}
