namespace M3Undle.Core.Providers;

public sealed class NormalizedProviderChannel
{
    public string? ProviderChannelKey { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? TvgId { get; init; }
    public string? TvgName { get; init; }
    public string? LogoUrl { get; init; }
    public string StreamUrl { get; init; } = string.Empty;
    public string? GroupTitle { get; init; }
}
