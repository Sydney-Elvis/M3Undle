namespace M3Undle.Web.Streaming.Models;

public sealed record StreamSourceDescriptor(
    string ProfileId,
    string ProviderId,
    string ProviderChannelId,
    string StreamUrl,
    string DisplayName,
    string RequestedRoute,
    string? UserAgent,
    string? RemoteIp,
    int? TunerLimit = null,
    bool ForceMpegTs = false)
{
    public ChannelSessionKey SessionKey => new(ProviderId, ProviderChannelId);
}

