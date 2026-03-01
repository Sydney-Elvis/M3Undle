using M3Undle.Core.M3u;
using M3Undle.Web.Api;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;

namespace M3Undle.Web.Tests.Snapshots;

[TestClass]
public sealed class SnapshotHandlingTests
{
    // -------------------------------------------------------------------------
    // UpsertProviderGroupsAsync
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task UpsertProviderGroups_NewEntries_CreatesGroups()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(NewProvider("p1"));
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var channels = new[]
        {
            NewChannel("Sports News", "Sports"),
            NewChannel("World News", "News"),
        };

        await ProviderApiEndpoints.UpsertProviderGroupsAsync(db, "p1", channels, DateTime.UtcNow, CancellationToken.None);

        var groups = await db.ProviderGroups.OrderBy(x => x.RawName).ToListAsync();
        Assert.HasCount(2, groups);
        Assert.IsTrue(groups.All(x => x.Active));
        Assert.AreEqual("News", groups[0].RawName);
        Assert.AreEqual("Sports", groups[1].RawName);
    }

    [TestMethod]
    public async Task UpsertProviderGroups_Rerun_UpdatesLastSeen()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(NewProvider("p1"));
            await setup.SaveChangesAsync();
        }

        var firstTime = DateTime.UtcNow.AddHours(-1);
        var secondTime = DateTime.UtcNow;
        var channels = new[] { NewChannel("CNN", "News") };

        await using (var db1 = fixture.CreateDbContext())
        {
            await ProviderApiEndpoints.UpsertProviderGroupsAsync(db1, "p1", channels, firstTime, CancellationToken.None);
        }

        await using (var db2 = fixture.CreateDbContext())
        {
            await ProviderApiEndpoints.UpsertProviderGroupsAsync(db2, "p1", channels, secondTime, CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var group = await verify.ProviderGroups.SingleAsync();
        Assert.AreEqual(1, await verify.ProviderGroups.CountAsync());
        Assert.IsTrue(group.LastSeenUtc >= secondTime.AddSeconds(-1));
    }

    [TestMethod]
    public async Task UpsertProviderGroups_MissingFromNewRun_MarkedInactive()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(NewProvider("p1"));
            await setup.SaveChangesAsync();
        }

        var now = DateTime.UtcNow;

        await using (var db1 = fixture.CreateDbContext())
        {
            await ProviderApiEndpoints.UpsertProviderGroupsAsync(
                db1, "p1",
                [NewChannel("CNN", "News"), NewChannel("ESPN", "Sports")],
                now, CancellationToken.None);
        }

        await using (var db2 = fixture.CreateDbContext())
        {
            await ProviderApiEndpoints.UpsertProviderGroupsAsync(
                db2, "p1",
                [NewChannel("ESPN", "Sports")],
                now.AddHours(1), CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var groups = await verify.ProviderGroups.ToListAsync();
        Assert.IsFalse(groups.Single(x => x.RawName == "News").Active);
        Assert.IsTrue(groups.Single(x => x.RawName == "Sports").Active);
    }

    // -------------------------------------------------------------------------
    // UpsertProviderChannelsAsync
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task UpsertProviderChannels_WhitespaceKey_SavedAsNull()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(NewProvider("p1"));
            setup.FetchRuns.Add(NewFetchRun("f1", "p1"));
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var channels = new[]
        {
            new ParsedProviderChannel { ProviderChannelKey = "   ", DisplayName = "CNN", StreamUrl = "http://example.com/cnn", GroupTitle = "News" },
        };

        await ProviderApiEndpoints.UpsertProviderGroupsAsync(db, "p1", channels, DateTime.UtcNow, CancellationToken.None);
        await ProviderApiEndpoints.UpsertProviderChannelsAsync(db, "p1", "f1", channels, DateTime.UtcNow, CancellationToken.None);
        await db.SaveChangesAsync();

        var saved = await db.ProviderChannels.SingleAsync();
        Assert.IsNull(saved.ProviderChannelKey);
    }

    [TestMethod]
    public async Task UpsertProviderChannels_KeyedChannel_IsUpserted()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(NewProvider("p1"));
            setup.FetchRuns.Add(NewFetchRun("f1", "p1"));
            setup.FetchRuns.Add(NewFetchRun("f2", "p1"));
            await setup.SaveChangesAsync();
        }

        var first = new ParsedProviderChannel { ProviderChannelKey = "cnn.us", DisplayName = "CNN Old", StreamUrl = "http://old.com", GroupTitle = null };
        var updated = new ParsedProviderChannel { ProviderChannelKey = "cnn.us", DisplayName = "CNN New", StreamUrl = "http://new.com", GroupTitle = null };

        await using (var db1 = fixture.CreateDbContext())
        {
            await ProviderApiEndpoints.UpsertProviderGroupsAsync(db1, "p1", [first], DateTime.UtcNow, CancellationToken.None);
            await ProviderApiEndpoints.UpsertProviderChannelsAsync(db1, "p1", "f1", [first], DateTime.UtcNow, CancellationToken.None);
            await db1.SaveChangesAsync();
        }

        await using (var db2 = fixture.CreateDbContext())
        {
            await ProviderApiEndpoints.UpsertProviderGroupsAsync(db2, "p1", [updated], DateTime.UtcNow, CancellationToken.None);
            await ProviderApiEndpoints.UpsertProviderChannelsAsync(db2, "p1", "f2", [updated], DateTime.UtcNow, CancellationToken.None);
            await db2.SaveChangesAsync();
        }

        await using var verify = fixture.CreateDbContext();
        var saved = await verify.ProviderChannels.SingleAsync();
        Assert.AreEqual("cnn.us", saved.ProviderChannelKey);
        Assert.AreEqual("CNN New", saved.DisplayName);
        Assert.AreEqual("http://new.com", saved.StreamUrl);
    }

    [TestMethod]
    public async Task UpsertProviderChannels_KeysDifferingByWhitespace_AreDeduplicatedByNormalizedKey()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(NewProvider("p1"));
            setup.FetchRuns.Add(NewFetchRun("f1", "p1"));
            await setup.SaveChangesAsync();
        }

        var first = new ParsedProviderChannel { ProviderChannelKey = "cnn.us", DisplayName = "CNN One", StreamUrl = "http://one.example.com", GroupTitle = "News" };
        var second = new ParsedProviderChannel { ProviderChannelKey = " cnn.us ", DisplayName = "CNN Two", StreamUrl = "http://two.example.com", GroupTitle = "News" };

        await using (var db = fixture.CreateDbContext())
        {
            await ProviderApiEndpoints.UpsertProviderGroupsAsync(db, "p1", [first, second], DateTime.UtcNow, CancellationToken.None);
            await ProviderApiEndpoints.UpsertProviderChannelsAsync(db, "p1", "f1", [first, second], DateTime.UtcNow, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var verify = fixture.CreateDbContext();
        var channels = await verify.ProviderChannels.ToListAsync();
        Assert.HasCount(1, channels);
        Assert.AreEqual("cnn.us", channels[0].ProviderChannelKey);
        Assert.AreEqual("CNN Two", channels[0].DisplayName);
        Assert.AreEqual("http://two.example.com", channels[0].StreamUrl);
    }

    [TestMethod]
    public async Task UpsertProviderChannels_NullKeyChannel_DeduplicatedByCompositeOnRerun()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(NewProvider("p1"));
            setup.FetchRuns.Add(NewFetchRun("f1", "p1"));
            setup.FetchRuns.Add(NewFetchRun("f2", "p1"));
            await setup.SaveChangesAsync();
        }

        var channel = new ParsedProviderChannel { ProviderChannelKey = null, DisplayName = "Unnamed Stream", StreamUrl = "http://example.com/s1", GroupTitle = "Sports" };

        await using (var db1 = fixture.CreateDbContext())
        {
            await ProviderApiEndpoints.UpsertProviderGroupsAsync(db1, "p1", [channel], DateTime.UtcNow, CancellationToken.None);
            await ProviderApiEndpoints.UpsertProviderChannelsAsync(db1, "p1", "f1", [channel], DateTime.UtcNow, CancellationToken.None);
            await db1.SaveChangesAsync();
        }

        await using (var db2 = fixture.CreateDbContext())
        {
            await ProviderApiEndpoints.UpsertProviderGroupsAsync(db2, "p1", [channel], DateTime.UtcNow, CancellationToken.None);
            await ProviderApiEndpoints.UpsertProviderChannelsAsync(db2, "p1", "f2", [channel], DateTime.UtcNow, CancellationToken.None);
            await db2.SaveChangesAsync();
        }

        await using var verify = fixture.CreateDbContext();
        Assert.AreEqual(1, await verify.ProviderChannels.CountAsync());
    }

    [TestMethod]
    public async Task UpsertProviderChannels_ChannelMissingFromNewRun_MarkedInactive()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(NewProvider("p1"));
            setup.FetchRuns.Add(NewFetchRun("f1", "p1"));
            setup.FetchRuns.Add(NewFetchRun("f2", "p1"));
            await setup.SaveChangesAsync();
        }

        var cnn = new ParsedProviderChannel { ProviderChannelKey = "cnn.us", DisplayName = "CNN", StreamUrl = "http://example.com/cnn", GroupTitle = "News" };
        var espn = new ParsedProviderChannel { ProviderChannelKey = "espn.hd", DisplayName = "ESPN", StreamUrl = "http://example.com/espn", GroupTitle = "Sports" };

        await using (var db1 = fixture.CreateDbContext())
        {
            await ProviderApiEndpoints.UpsertProviderGroupsAsync(db1, "p1", [cnn, espn], DateTime.UtcNow, CancellationToken.None);
            await ProviderApiEndpoints.UpsertProviderChannelsAsync(db1, "p1", "f1", [cnn, espn], DateTime.UtcNow, CancellationToken.None);
            await db1.SaveChangesAsync();
        }

        await using (var db2 = fixture.CreateDbContext())
        {
            await ProviderApiEndpoints.UpsertProviderGroupsAsync(db2, "p1", [espn], DateTime.UtcNow, CancellationToken.None);
            await ProviderApiEndpoints.UpsertProviderChannelsAsync(db2, "p1", "f2", [espn], DateTime.UtcNow, CancellationToken.None);
            await db2.SaveChangesAsync();
        }

        await using var verify = fixture.CreateDbContext();
        var channels = await verify.ProviderChannels.ToListAsync();
        Assert.IsFalse(channels.Single(x => x.ProviderChannelKey == "cnn.us").Active);
        Assert.IsTrue(channels.Single(x => x.ProviderChannelKey == "espn.hd").Active);
    }

    // -------------------------------------------------------------------------
    // SnapshotBuilder.RunAsync
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task SnapshotBuilder_NoActiveProvider_NoFetchRunCreated()
    {
        await using var fixture = await CreateFixtureAsync();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using var db = fixture.CreateDbContext();
            var builder = CreateBuilder(db, HttpStatusCode.OK, "", tempDir);
            await builder.RunAsync(CancellationToken.None);

            Assert.AreEqual(0, await db.FetchRuns.CountAsync());
            Assert.AreEqual(0, await db.Snapshots.CountAsync());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_FetchFails_RecordsFetchRunAsFail_NoSnapshot()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            setup.Providers.Add(NewProvider("provider-1", active: true));
            setup.ProfileProviders.Add(NewProfileProvider("provider-1", "profile-1"));
            await setup.SaveChangesAsync();
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using var db = fixture.CreateDbContext();
            var builder = CreateBuilder(db, HttpStatusCode.InternalServerError, "upstream error", tempDir);
            await builder.RunAsync(CancellationToken.None);

            await using var verify = fixture.CreateDbContext();
            var fetchRun = await verify.FetchRuns.SingleAsync();
            Assert.AreEqual("fail", fetchRun.Status);
            Assert.IsNotNull(fetchRun.ErrorSummary);
            Assert.AreEqual(0, await verify.Snapshots.CountAsync());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_FetchSucceeds_PromotesSnapshotToActive()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            setup.Providers.Add(NewProvider("provider-1", active: true));
            setup.ProfileProviders.Add(NewProfileProvider("provider-1", "profile-1"));
            await setup.SaveChangesAsync();
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using var db = fixture.CreateDbContext();
            var builder = CreateBuilder(db, HttpStatusCode.OK, SampleM3u, tempDir);
            await builder.RunAsync(CancellationToken.None);

            await using var verify = fixture.CreateDbContext();
            var fetchRun = await verify.FetchRuns.SingleAsync();
            Assert.AreEqual("ok", fetchRun.Status);
            Assert.IsNull(fetchRun.ErrorSummary);

            var snapshot = await verify.Snapshots.SingleAsync();
            Assert.AreEqual("active", snapshot.Status);
            Assert.IsTrue(File.Exists(snapshot.ChannelIndexPath));
            Assert.IsTrue(File.Exists(snapshot.XmltvPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_SecondFetch_ArchivesPreviousSnapshot()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            setup.Providers.Add(NewProvider("provider-1", active: true));
            setup.ProfileProviders.Add(NewProfileProvider("provider-1", "profile-1"));
            await setup.SaveChangesAsync();
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using (var db1 = fixture.CreateDbContext())
            {
                await CreateBuilder(db1, HttpStatusCode.OK, SampleM3u, tempDir).RunAsync(CancellationToken.None);
            }

            await using (var db2 = fixture.CreateDbContext())
            {
                await CreateBuilder(db2, HttpStatusCode.OK, SampleM3u, tempDir).RunAsync(CancellationToken.None);
            }

            await using var verify = fixture.CreateDbContext();
            var snapshots = await verify.Snapshots.OrderByDescending(x => x.CreatedUtc).ToListAsync();
            Assert.HasCount(2, snapshots);
            Assert.AreEqual("active", snapshots[0].Status);
            Assert.AreEqual("archived", snapshots[1].Status);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_IncludesVodAndSeries_WhenEnabled_EvenIfGroupsPending()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            var provider = NewProvider("provider-1", active: true);
            provider.IncludeVod = true;
            provider.IncludeSeries = true;
            setup.Providers.Add(provider);
            setup.ProfileProviders.Add(NewProfileProvider("provider-1", "profile-1"));
            await setup.SaveChangesAsync();
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using (var db1 = fixture.CreateDbContext())
            {
                await CreateBuilder(db1, HttpStatusCode.OK, SampleMixedM3u, tempDir).RunAsync(CancellationToken.None);
            }

            await using (var verify1 = fixture.CreateDbContext())
            {
                var active1 = await verify1.Snapshots.SingleAsync(x => x.Status == "active");
                Assert.AreEqual(2, active1.ChannelCountPublished, "First build should publish VOD+series passthrough while live remains unmapped.");
            }

            await using (var edit = fixture.CreateDbContext())
            {
                var newsFilter = await edit.ProfileGroupFilters
                    .Include(x => x.ProviderGroup)
                    .SingleAsync(x => x.ProfileId == "profile-1" && x.ProviderGroup.RawName == "News");
                newsFilter.Decision = "include";
                newsFilter.UpdatedUtc = DateTime.UtcNow;

                var moviesFilter = await edit.ProfileGroupFilters
                    .Include(x => x.ProviderGroup)
                    .SingleAsync(x => x.ProfileId == "profile-1" && x.ProviderGroup.RawName == "Movies");
                moviesFilter.Decision = "exclude";
                moviesFilter.UpdatedUtc = DateTime.UtcNow;

                var seriesFilter = await edit.ProfileGroupFilters
                    .Include(x => x.ProviderGroup)
                    .SingleAsync(x => x.ProfileId == "profile-1" && x.ProviderGroup.RawName == "Series");
                seriesFilter.Decision = "exclude";
                seriesFilter.UpdatedUtc = DateTime.UtcNow;

                await edit.SaveChangesAsync();
            }

            await using (var db2 = fixture.CreateDbContext())
            {
                await CreateBuilder(db2, HttpStatusCode.OK, SampleMixedM3u, tempDir).RunAsync(CancellationToken.None);
            }

            await using var verify2 = fixture.CreateDbContext();
            var active2 = await verify2.Snapshots
                .Where(x => x.Status == "active")
                .OrderByDescending(x => x.CreatedUtc)
                .FirstAsync();
            Assert.AreEqual(3, active2.ChannelCountPublished, "Second build should add included live channels while VOD+series remain controlled only by provider Include flags.");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Test helpers
    // -------------------------------------------------------------------------

    private const string SampleM3u =
        "#EXTM3U\n" +
        "#EXTINF:-1 tvg-id=\"cnn.us\" tvg-name=\"CNN\" group-title=\"News\",CNN US\n" +
        "http://example.com/stream/cnn\n";

    private const string SampleMixedM3u =
        "#EXTM3U\n" +
        "#EXTINF:-1 group-title=\"News\",Live News\n" +
        "http://example.com/live/user/pass/100.ts\n" +
        "#EXTINF:-1 group-title=\"Movies\",Movie One\n" +
        "http://example.com/movie/user/pass/200.mkv\n" +
        "#EXTINF:-1 group-title=\"Series\",Series One\n" +
        "http://example.com/series/user/pass/300.mkv\n";

    private static SnapshotBuilder CreateBuilder(ApplicationDbContext db, HttpStatusCode statusCode, string content, string tempDir)
    {
        var handler = new FakeHttpMessageHandler(statusCode, content);
        var factory = new FakeHttpClientFactory(handler);
        var envSvc = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var fetcher = new ProviderFetcher(factory, new PlaylistParser(), envSvc, NullLogger<ProviderFetcher>.Instance);
        var env = new FakeWebHostEnvironment(tempDir);
        return new SnapshotBuilder(db, fetcher, env, Options.Create(new SnapshotOptions()), NullLogger<SnapshotBuilder>.Instance);
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        var fixture = new TestFixture(connection, options);
        await using var db = fixture.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        return fixture;
    }

    private static Profile NewProfile(string id) => new()
    {
        ProfileId = id,
        Name = id,
        Enabled = true,
        OutputName = id,
        MergeMode = "single",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static Provider NewProvider(string id, bool active = false) => new()
    {
        ProviderId = id,
        Name = id,
        Enabled = true,
        IsActive = active,
        PlaylistUrl = "http://example.com/playlist.m3u",
        XmltvUrl = "http://example.com/xmltv.xml",
        TimeoutSeconds = 20,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static ProfileProvider NewProfileProvider(string providerId, string profileId) => new()
    {
        ProviderId = providerId,
        ProfileId = profileId,
        Priority = 1,
        Enabled = true,
    };

    private static FetchRun NewFetchRun(string id, string providerId) => new()
    {
        FetchRunId = id,
        ProviderId = providerId,
        StartedUtc = DateTime.UtcNow,
        Status = "ok",
    };

    private static ParsedProviderChannel NewChannel(string displayName, string? groupTitle) => new()
    {
        DisplayName = displayName,
        StreamUrl = $"http://example.com/stream/{displayName.ToLowerInvariant().Replace(" ", "-")}",
        GroupTitle = groupTitle,
    };

    private sealed class TestFixture(SqliteConnection connection, DbContextOptions<ApplicationDbContext> options) : IAsyncDisposable
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class FakeHttpMessageHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content),
            });
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "test";
        public string EnvironmentName { get; set; } = "test";
    }
}
