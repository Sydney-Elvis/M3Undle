using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.HdHomeRun;

[TestClass]
public sealed class HdHomeRunLineupServiceTests
{
    [TestMethod]
    public async Task TryBuildActiveLineupAsync_ReturnsOnlyLiveChannelsInStableOrder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"m3undle-hdhr-lineup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var db = new ApplicationDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var snapshotDir = Path.Combine(tempDir, "snapshot");
            Directory.CreateDirectory(snapshotDir);
            var channelIndexPath = Path.Combine(snapshotDir, "channel_index.ndjson");
            var channelIndexIdxPath = Path.Combine(snapshotDir, "channel_index.idx");

            var entries = new List<ChannelIndexEntry>
            {
                new("live-1", "Alpha", "alpha.tv", "Alpha", null, "News", 11, "p1", "http://example.com/live/alpha.ts"),
                new("vod-1", "Movie One", "movie.one", null, null, "Movies", null, "p2", "http://example.com/movie/one.mkv"),
                new("live-2", "Bravo", "bravo.tv", "Bravo Name", null, "News", null, "p3", "http://example.com/live/bravo.ts"),
            };

            await ChannelIndexStore.WriteAsync(channelIndexPath, channelIndexIdxPath, entries, CancellationToken.None);

            db.Profiles.Add(new Profile
            {
                ProfileId = "profile-1",
                Name = "profile-1",
                Enabled = true,
                OutputName = "profile-1",
                MergeMode = "single",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });

            db.Snapshots.Add(new Snapshot
            {
                SnapshotId = "snapshot-1",
                ProfileId = "profile-1",
                CreatedUtc = DateTime.UtcNow,
                Status = "active",
                PlaylistPath = string.Empty,
                XmltvPath = string.Empty,
                ChannelIndexPath = channelIndexPath,
                StatusJsonPath = string.Empty,
                ChannelCountPublished = 3,
                LiveChannelCount = 2,
                VodChannelCount = 1,
                SeriesChannelCount = 0,
            });
            await db.SaveChangesAsync();

            var service = new HdHomeRunLineupService(db);
            var lineup = await service.TryBuildActiveLineupAsync("http://test-host:8080", CancellationToken.None);

            Assert.IsNotNull(lineup);
            Assert.HasCount(2, lineup.Channels);

            var first = lineup.Channels[0];
            var second = lineup.Channels[1];

            Assert.AreEqual("live-1", first.ChannelId);
            Assert.AreEqual("11", first.GuideNumber);
            Assert.AreEqual("Alpha", first.GuideName);
            Assert.AreEqual("http://test-host:8080/tune/live-1", first.Url);

            Assert.AreEqual("live-2", second.ChannelId);
            Assert.AreEqual("1000", second.GuideNumber);
            Assert.AreEqual("Bravo Name", second.GuideName);
            Assert.AreEqual("http://test-host:8080/tune/live-2", second.Url);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task TryBuildActiveLineupAsync_NoSnapshot_ReturnsNull()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var service = new HdHomeRunLineupService(db);
        var lineup = await service.TryBuildActiveLineupAsync("http://test-host:8080", CancellationToken.None);

        Assert.IsNull(lineup);
    }
}
