using System.Globalization;
using M3Undle.Web.Security;

namespace M3Undle.Web.Application;

public sealed record HdHomeRunLineupEntry(
    string ChannelId,
    string GuideNumber,
    string GuideName,
    string Url,
    string? TvgId,
    string? LogoUrl);

public sealed record HdHomeRunLineupResult(
    string SnapshotId,
    DateTime SnapshotCreatedUtc,
    IReadOnlyList<HdHomeRunLineupEntry> Channels);

public sealed class HdHomeRunLineupService
{
    public async Task<HdHomeRunLineupResult?> TryBuildActiveLineupAsync(
        string baseUrl,
        RenderedLineup lineup,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var channels = new List<HdHomeRunLineupEntry>();
        foreach (var resolved in EnumerateResolvedLiveChannels(lineup, cancellationToken))
        {
            channels.Add(new HdHomeRunLineupEntry(
                ChannelId: resolved.Channel.StreamKey,
                GuideNumber: resolved.GuideNumber,
                GuideName: resolved.GuideName,
                Url: $"{baseUrl}/hdhr/tune/{resolved.Channel.StreamKey}".ApplyClientAccessQuery(context),
                TvgId: resolved.Channel.TvgId,
                LogoUrl: resolved.Channel.LogoUrl));
        }

        return new HdHomeRunLineupResult(lineup.SnapshotId, lineup.SnapshotCreatedUtc, channels);
    }

    public string? TryResolveStreamKeyByGuideNumber(
        RenderedLineup lineup,
        string guideNumber,
        CancellationToken cancellationToken)
    {
        var normalizedGuideNumber = guideNumber.Trim();
        if (string.IsNullOrWhiteSpace(normalizedGuideNumber))
            return null;

        foreach (var resolved in EnumerateResolvedLiveChannels(lineup, cancellationToken))
        {
            if (string.Equals(resolved.GuideNumber, normalizedGuideNumber, StringComparison.Ordinal))
                return resolved.Channel.StreamKey;
        }

        return null;
    }

    private static IEnumerable<ResolvedLiveChannel> EnumerateResolvedLiveChannels(
        RenderedLineup lineup,
        CancellationToken cancellationToken)
    {
        var fallbackGuideNumber = 1000;

        foreach (var channel in lineup.Channels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (channel.ContentType != "live")
                continue;

            var guideNumber = channel.TvgChno.HasValue
                ? channel.TvgChno.Value.ToString(CultureInfo.InvariantCulture)
                : (fallbackGuideNumber++).ToString(CultureInfo.InvariantCulture);

            var guideName = string.IsNullOrWhiteSpace(channel.TvgName)
                ? channel.DisplayName
                : channel.TvgName;

            yield return new ResolvedLiveChannel(channel, guideNumber, guideName);
        }
    }

    private sealed record ResolvedLiveChannel(RenderedLineupChannel Channel, string GuideNumber, string GuideName);
}
