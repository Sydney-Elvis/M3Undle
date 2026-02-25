namespace M3Undle.Web.Application;

/// <summary>
/// One entry in channel_index.json — written per snapshot.
/// StreamUrl is included so the relay endpoint can work from file without a DB round-trip.
/// </summary>
public sealed record ChannelIndexEntry(
    string StreamKey,
    string DisplayName,
    string? TvgId,
    string? TvgName,
    string? LogoUrl,
    string? GroupTitle,
    int? TvgChno,
    string ProviderChannelId,
    string StreamUrl);

