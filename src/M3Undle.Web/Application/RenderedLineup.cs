using M3Undle.Core.M3u;
using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public sealed record RenderedLineupChannel(
    string StreamKey,
    string DisplayName,
    string? TvgId,
    string? TvgName,
    string? LogoUrl,
    string? GroupTitle,
    int? TvgChno,
    string StreamUrl,
    string ContentType);

public sealed record RenderedLineup(
    string SnapshotId,
    string ProfileId,
    DateTime SnapshotCreatedUtc,
    string ChannelIndexPath,
    string? XmltvPath,
    IReadOnlyList<RenderedLineupChannel> Channels);

public interface ILineupRenderer
{
    Task<RenderedLineup?> TryRenderActiveLineupAsync(string? profileId, CancellationToken cancellationToken);
}

public sealed class ActiveSnapshotLineupRenderer(ApplicationDbContext db) : ILineupRenderer
{
    public async Task<RenderedLineup?> TryRenderActiveLineupAsync(string? profileId, CancellationToken cancellationToken)
    {
        var snapshots = db.Snapshots
            .AsNoTracking()
            .Where(x => x.Status == "active");
        if (!string.IsNullOrWhiteSpace(profileId))
            snapshots = snapshots.Where(x => x.ProfileId == profileId);

        var snapshot = await snapshots
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.ChannelIndexPath))
            return null;

        if (!File.Exists(snapshot.ChannelIndexPath))
            throw new IOException("Active snapshot channel index path does not exist.");

        var channels = new List<RenderedLineupChannel>();
        await foreach (var entry in ChannelIndexStore.StreamAllAsync(snapshot.ChannelIndexPath, cancellationToken))
        {
            var contentType = LiveClassifier.ClassifyContent(entry.StreamUrl) switch
            {
                "vod" => "vod",
                "series" => "series",
                _ => "live",
            };

            channels.Add(new RenderedLineupChannel(
                StreamKey: entry.StreamKey,
                DisplayName: entry.DisplayName,
                TvgId: entry.TvgId,
                TvgName: entry.TvgName,
                LogoUrl: entry.LogoUrl,
                GroupTitle: entry.GroupTitle,
                TvgChno: entry.TvgChno,
                StreamUrl: entry.StreamUrl,
                ContentType: contentType));
        }

        return new RenderedLineup(
            SnapshotId: snapshot.SnapshotId,
            ProfileId: snapshot.ProfileId,
            SnapshotCreatedUtc: snapshot.CreatedUtc,
            ChannelIndexPath: snapshot.ChannelIndexPath,
            XmltvPath: snapshot.XmltvPath,
            Channels: channels);
    }
}
