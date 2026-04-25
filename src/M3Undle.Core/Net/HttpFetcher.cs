namespace M3Undle.Core.Net;

public static class HttpFetcher
{
    /// <summary>
    /// Sends a GET request to <paramref name="uri"/>, logs response headers to
    /// <paramref name="diagnostics"/>, and throws <see cref="CoreException"/> for
    /// auth failures, non-2xx responses, timeouts, and network errors.
    /// Returns the validated <see cref="HttpResponseMessage"/> on success — the
    /// caller owns disposal.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAndValidateAsync(
        HttpClient client,
        Uri uri,
        TextWriter diagnostics,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            if (diagnostics != TextWriter.Null)
                await diagnostics.WriteLineAsync($"Downloading {UrlRedactor.RedactUrl(uri)}...");

            var response = await client.GetAsync(uri, completionOption, cancellationToken);

            if (diagnostics != TextWriter.Null)
            {
                await diagnostics.WriteLineAsync($"Response status: {(int)response.StatusCode} {response.ReasonPhrase}");
                await diagnostics.WriteLineAsync($"Content-Type: {response.Content.Headers.ContentType}");
                await diagnostics.WriteLineAsync($"Content-Length: {response.Content.Headers.ContentLength?.ToString() ?? "unknown"}");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                response.Dispose();
                throw new CoreException($"Authentication failed when requesting {UrlRedactor.RedactUrl(uri)}", ExitCodes.AuthError);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await ReadErrorBodyAsync(response, diagnostics, cancellationToken);
                response.Dispose();
                var errorMessage = $"Request to {UrlRedactor.RedactUrl(uri)} failed with status {(int)response.StatusCode} ({response.ReasonPhrase}).";
                if (!string.IsNullOrWhiteSpace(errorBody))
                    errorMessage += $"\nServer response: {errorBody}";
                throw new CoreException(errorMessage, ExitCodes.NetworkError);
            }

            return response;
        }
        catch (CoreException)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            if (diagnostics != TextWriter.Null)
                await diagnostics.WriteLineAsync($"Request timed out: {ex}");
            throw new CoreException($"Request to {UrlRedactor.RedactUrl(uri)} timed out: {ex.Message}", ExitCodes.NetworkError);
        }
        catch (HttpRequestException ex)
        {
            if (diagnostics != TextWriter.Null)
                await diagnostics.WriteLineAsync($"Request failed: {ex}");
            throw new CoreException($"Request to {UrlRedactor.RedactUrl(uri)} failed: {ex.Message}", ExitCodes.NetworkError);
        }
    }

    private static async Task<string> ReadErrorBodyAsync(
        HttpResponseMessage response, TextWriter diagnostics, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (diagnostics != TextWriter.Null && !string.IsNullOrWhiteSpace(body))
            {
                await diagnostics.WriteLineAsync("=== Server Error Response Body ===");
                await diagnostics.WriteLineAsync(body);
                await diagnostics.WriteLineAsync("=== End Server Error Response ===");
            }

            return !string.IsNullOrWhiteSpace(body) && body.Length > 500
                ? body[..500] + "..."
                : body ?? string.Empty;
        }
        catch (Exception ex)
        {
            if (diagnostics != TextWriter.Null)
                await diagnostics.WriteLineAsync($"Failed to read error response body: {ex.Message}");
            return string.Empty;
        }
    }
}
