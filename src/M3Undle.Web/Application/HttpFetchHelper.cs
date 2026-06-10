using System.Text;

namespace M3Undle.Web.Application;

// Streams an HTTP response body with an idle-activity timeout.
// Times out only when no bytes arrive for idleTimeoutSeconds — data still
// flowing never causes a timeout no matter how large the file.
internal static class HttpFetchHelper
{
    internal static async Task<string> FetchStringAsync(
        HttpClient client,
        string url,
        int idleTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var idleTimeout = TimeSpan.FromSeconds(idleTimeoutSeconds);

        HttpResponseMessage response;
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectCts.CancelAfter(idleTimeout);
            try
            {
                response = await client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Get, url),
                    HttpCompletionOption.ResponseHeadersRead,
                    connectCts.Token);
            }
            catch (OperationCanceledException) when (connectCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ProviderFetchException($"Fetch timed out after {idleTimeoutSeconds}s waiting for server response.");
            }
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            await using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var ms = new MemoryStream();
            var buffer = new byte[65536];

            while (true)
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idleCts.CancelAfter(idleTimeout);

                int bytesRead;
                try
                {
                    bytesRead = await bodyStream.ReadAsync(buffer.AsMemory(), idleCts.Token);
                }
                catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new ProviderFetchException($"Fetch timed out — no data received for {idleTimeoutSeconds}s.");
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
