using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Security;
using M3Undle.Web.Streaming.Resolution;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class StreamRequestResolverTests
{
    [TestMethod]
    public async Task ResolveAsync_LiveRoute_ReturnsSharedSessionDescriptor()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var resolver = new StreamRequestResolver(fixture.Db, NullLogger<StreamRequestResolver>.Instance);
        var context = CreateHttpContext("/live/key-live", "profile-1");

        var result = await resolver.ResolveAsync("key-live", context, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.UseSharedSession);
        Assert.IsNotNull(result.SourceDescriptor);
        Assert.AreEqual("provider-1", result.SourceDescriptor.ProviderId);
        Assert.AreEqual("provider-channel-1", result.SourceDescriptor.ProviderChannelId);
    }

    [TestMethod]
    public async Task ResolveAsync_MovieRoute_StaysDirectRelay()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var resolver = new StreamRequestResolver(fixture.Db, NullLogger<StreamRequestResolver>.Instance);
        var context = CreateHttpContext("/movie/key-live", "profile-1");

        var result = await resolver.ResolveAsync("key-live", context, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.UseSharedSession);
        Assert.IsNull(result.SourceDescriptor);
        Assert.IsNotNull(result.Entry);
    }

    private static DefaultHttpContext CreateHttpContext(string path, string profileId)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.SetResolvedClientAccess(new ResolvedClientAccess(
            Credential: new AccessCredential(
                Id: "cred-1",
                Username: "user",
                PasswordHash: "hash",
                Enabled: true,
                AuthType: AccessCredentialAuthType.UsernamePassword),
            Binding: new AccessBinding(
                CredentialId: "cred-1",
                ActiveProfileId: profileId,
                AllowedProfileIds: [profileId],
                VirtualTunerId: "hdhr-main"),
            Transport: ClientCredentialTransport.None,
            UrlCredential: null));
        return context;
    }

    private sealed class TestFixture(SqliteConnection connection, ApplicationDbContext db, string tempDirectory) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;

        public ApplicationDbContext Db { get; } = db;

        public string TempDirectory { get; } = tempDirectory;

        public static async Task<TestFixture> CreateAsync()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "m3undle-stream-resolver-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;

            var db = new ApplicationDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var profile = new Profile
            {
                ProfileId = "profile-1",
                Name = "Profile 1",
                Enabled = true,
                OutputName = "Profile 1",
                MergeMode = "default",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            };

            var provider = new Provider
            {
                ProviderId = "provider-1",
                Name = "Provider 1",
                Enabled = true,
                IsActive = true,
                PlaylistUrl = "http://provider.test/playlist.m3u",
                TimeoutSeconds = 30,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            };

            var profileProvider = new ProfileProvider
            {
                ProfileId = profile.ProfileId,
                ProviderId = provider.ProviderId,
                Priority = 0,
                Enabled = true,
            };

            var fetchRun = new FetchRun
            {
                FetchRunId = "fetch-1",
                ProviderId = provider.ProviderId,
                StartedUtc = DateTime.UtcNow,
                FinishedUtc = DateTime.UtcNow,
                Status = "ok",
                Type = "snapshot",
            };

            var providerChannel = new ProviderChannel
            {
                ProviderChannelId = "provider-channel-1",
                ProviderId = provider.ProviderId,
                ProviderChannelKey = "channel-key-1",
                DisplayName = "Test Channel",
                StreamUrl = "http://provider.test/live/channel1.ts",
                GroupTitle = "News",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
                Active = true,
                ContentType = "live",
                LastFetchRunId = fetchRun.FetchRunId,
            };

            var channelIndexPath = Path.Combine(tempDirectory, "channel_index.ndjson");
            var channelIndexIdxPath = Path.Combine(tempDirectory, "channel_index.idx");
            await ChannelIndexStore.WriteAsync(
                channelIndexPath,
                channelIndexIdxPath,
                [
                    new ChannelIndexEntry(
                        StreamKey: "key-live",
                        DisplayName: "Test Channel",
                        TvgId: null,
                        TvgName: null,
                        LogoUrl: null,
                        GroupTitle: "News",
                        TvgChno: 101,
                        ProviderChannelId: providerChannel.ProviderChannelId,
                        StreamUrl: providerChannel.StreamUrl),
                ],
                CancellationToken.None);

            var snapshot = new Snapshot
            {
                SnapshotId = "snapshot-1",
                ProfileId = profile.ProfileId,
                CreatedUtc = DateTime.UtcNow,
                Status = "active",
                PlaylistPath = Path.Combine(tempDirectory, "playlist.m3u"),
                XmltvPath = Path.Combine(tempDirectory, "guide.xml"),
                ChannelIndexPath = channelIndexPath,
                StatusJsonPath = Path.Combine(tempDirectory, "status.json"),
                ChannelCountPublished = 1,
                LiveChannelCount = 1,
                VodChannelCount = 0,
                SeriesChannelCount = 0,
            };

            db.Profiles.Add(profile);
            db.Providers.Add(provider);
            db.ProfileProviders.Add(profileProvider);
            db.FetchRuns.Add(fetchRun);
            db.ProviderChannels.Add(providerChannel);
            db.Snapshots.Add(snapshot);
            await db.SaveChangesAsync();

            return new TestFixture(connection, db, tempDirectory);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
            if (Directory.Exists(TempDirectory))
                Directory.Delete(TempDirectory, recursive: true);
        }
    }
}

