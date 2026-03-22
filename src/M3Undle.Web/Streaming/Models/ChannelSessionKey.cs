namespace M3Undle.Web.Streaming.Models;

public readonly record struct ChannelSessionKey
{
    public ChannelSessionKey(string providerId, string providerChannelId)
    {
        ProviderId = Normalize(providerId);
        ProviderChannelId = Normalize(providerChannelId);
    }

    public string ProviderId { get; }

    public string ProviderChannelId { get; }

    public override string ToString() => $"{ProviderId}:{ProviderChannelId}";

    private static string Normalize(string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
}

