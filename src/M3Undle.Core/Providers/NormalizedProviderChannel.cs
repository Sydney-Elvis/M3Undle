namespace M3Undle.Core.Providers;

public sealed record NormalizedProviderChannel(
    string? ProviderChannelKey,
    string DisplayName,
    string? TvgId,
    string? TvgName,
    string? LogoUrl,
    string StreamUrl,
    string? GroupTitle);
