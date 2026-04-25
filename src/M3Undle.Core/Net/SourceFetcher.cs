namespace M3Undle.Core.Net;

public sealed class SourceFetcher
{
    private readonly HttpClient _httpClient;
    private readonly TextWriter _diagnostics;

    public SourceFetcher(HttpClient httpClient, TextWriter diagnostics)
    {
        _httpClient = httpClient;
        _diagnostics = diagnostics;
    }

    public async Task<string> GetStringAsync(string source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new CoreException("Playlist URL was not provided.", ExitCodes.ConfigError);
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var response = await HttpFetcher.SendAndValidateAsync(_httpClient, uri, _diagnostics, cancellationToken);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        try
        {
            if (_diagnostics != TextWriter.Null)
            {
                await _diagnostics.WriteLineAsync($"Reading file {source}...");
            }

            return await File.ReadAllTextAsync(source, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CoreException($"Failed to read file {source}: {ex.Message}", ExitCodes.IoError);
        }
    }
}
