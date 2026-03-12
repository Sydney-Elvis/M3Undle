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

            channels.Add(new HdHomeRunLineupEntry(
                ChannelId: channel.StreamKey,
                GuideNumber: guideNumber,
                GuideName: guideName,
                Url: $"{baseUrl}/hdhr/tune/{channel.StreamKey}".ApplyClientAccessQuery(context),
                TvgId: channel.TvgId,
                LogoUrl: channel.LogoUrl));
        }

        return new HdHomeRunLineupResult(lineup.SnapshotId, lineup.SnapshotCreatedUtc, channels);
    }
}
