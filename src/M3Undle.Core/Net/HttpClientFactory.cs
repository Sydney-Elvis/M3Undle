namespace M3Undle.Core.Net;

public static class HttpClientFactory
{
    public static HttpClient CreateDefault()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("M3Undle/0.1");
        return client;
    }
}

