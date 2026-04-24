using System.Text.Json;

namespace M3Undle.Core.Providers;

public static class ProviderRequestHeaders
{
    public static IReadOnlyList<ProviderRequestHeader> ParseJson(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return [];
        }

        using var document = JsonDocument.Parse(headersJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var headers = new List<ProviderRequestHeader>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            headers.Add(new ProviderRequestHeader(property.Name, value));
        }

        return headers;
    }

    public static void ApplyTo(HttpClient client, string? headersJson)
    {
        ArgumentNullException.ThrowIfNull(client);

        foreach (var header in ParseJson(headersJson))
        {
            client.DefaultRequestHeaders.Remove(header.Name);
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Name, header.Value);
        }
    }
}
