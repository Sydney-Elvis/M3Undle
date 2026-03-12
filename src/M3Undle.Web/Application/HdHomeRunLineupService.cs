using System.Globalization;
using M3Undle.Core.M3u;
using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;

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

public sealed class HdHomeRunLineupService(ApplicationDbContext db)
{
    public async Task<HdHomeRunLineupResult?> TryBuildActiveLineupAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var snapshot = await db.Snapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Status == "active", cancellationToken);

        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.ChannelIndexPath))
            return null;
        if (!File.Exists(snapshot.ChannelIndexPath))
            throw new IOException("Active snapshot channel index path does not exist.");

        var channels = new List<HdHomeRunLineupEntry>();
        var fallbackGuideNumber = 1000;

        await foreach (var channel in ChannelIndexStore.StreamAllAsync(snapshot.ChannelIndexPath, cancellationToken))
        {
            if (LiveClassifier.ClassifyContent(channel.StreamUrl) != "live")
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
                Url: $"{baseUrl}/tune/{channel.StreamKey}",
                TvgId: channel.TvgId,
                LogoUrl: channel.LogoUrl));
        }

        return new HdHomeRunLineupResult(snapshot.SnapshotId, snapshot.CreatedUtc, channels);
    }
}

