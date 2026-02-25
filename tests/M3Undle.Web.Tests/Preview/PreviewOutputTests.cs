using M3Undle.Web.Api;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Preview;

[TestClass]
public sealed class PreviewOutputTests
{
    // -------------------------------------------------------------------------
    // BuildPreviewAsync
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task BuildPreview_GroupsChannelsCorrectly()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        await SeedChannelsAsync(db, "p1", "f1",
        [
            ("CNN US", (string?)"News", "http://example.com/cnn"),
            ("Fox News", (string?)"News", "http://example.com/fox"),
            ("ESPN", (string?)"Sports", "http://example.com/espn"),
        ]);

        var result = await ProviderApiEndpoints.BuildPreviewAsync(
            db, "p1", "f1", DateTime.UtcNow, sampleSize: 10, groupContains: null, CancellationToken.None);

        Assert.AreEqual(2, result.Totals.GroupCount);
        Assert.AreEqual(3, result.Totals.ChannelCount);

        var news = result.Groups.Single(g => g.GroupName == "News");
        Assert.AreEqual(2, news.ChannelCount);

        var sports = result.Groups.Single(g => g.GroupName == "Sports");
        Assert.AreEqual(1, sports.ChannelCount);
    }

    [TestMethod]
    public async Task BuildPreview_GroupFilter_IsCaseInsensitive()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        await SeedChannelsAsync(db, "p1", "f1",
        [
            ("CNN US", (string?)"News", "http://example.com/cnn"),
            ("ESPN", (string?)"Sports", "http://example.com/espn"),
        ]);

        var result = await ProviderApiEndpoints.BuildPreviewAsync(
            db, "p1", "f1", DateTime.UtcNow, sampleSize: 10, groupContains: "NEWS", CancellationToken.None);

        Assert.AreEqual(1, result.Totals.GroupCount);
        Assert.AreEqual("News", result.Groups.Single().GroupName);
    }

    [TestMethod]
    public async Task BuildPreview_SampleSize_LimitsChannelsPerGroup()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        await SeedChannelsAsync(db, "p1", "f1",
        [
            ("Alpha", (string?)"Sports", "http://example.com/a"),
            ("Beta", (string?)"Sports", "http://example.com/b"),
            ("Gamma", (string?)"Sports", "http://example.com/c"),
        ]);

        var result = await ProviderApiEndpoints.BuildPreviewAsync(
            db, "p1", "f1", DateTime.UtcNow, sampleSize: 2, groupContains: null, CancellationToken.None);

        var group = result.Groups.Single();
        Assert.AreEqual(3, group.ChannelCount);
        Assert.HasCount(2, group.SampleChannels);
    }

    [TestMethod]
    public async Task BuildPreview_ChannelWithNoGroup_AppearsAsUngrouped()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        await SeedChannelsAsync(db, "p1", "f1",
        [
            ("Mystery Stream", (string?)null, "http://example.com/mystery"),
        ]);

        var result = await ProviderApiEndpoints.BuildPreviewAsync(
            db, "p1", "f1", DateTime.UtcNow, sampleSize: 10, groupContains: null, CancellationToken.None);

        Assert.AreEqual("(Ungrouped)", result.Groups.Single().GroupName);
    }

    [TestMethod]
    public async Task BuildPreview_ChannelHasStreamUrl_HasStreamUrlIsTrue()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        await SeedChannelsAsync(db, "p1", "f1",
        [
            ("CNN", (string?)"News", "http://example.com/cnn"),
        ]);

        var result = await ProviderApiEndpoints.BuildPreviewAsync(
            db, "p1", "f1", DateTime.UtcNow, sampleSize: 10, groupContains: null, CancellationToken.None);

        var channel = result.Groups.Single().SampleChannels.Single();
        Assert.IsTrue(channel.HasStreamUrl);
    }

    // -------------------------------------------------------------------------
    // RedactStreamUrl
    // -------------------------------------------------------------------------

    [TestMethod]
    public void RedactStreamUrl_Null_ReturnsNull()
        => Assert.IsNull(ProviderApiEndpoints.RedactStreamUrl(null));

    [TestMethod]
    public void RedactStreamUrl_Empty_ReturnsNull()
        => Assert.IsNull(ProviderApiEndpoints.RedactStreamUrl(""));

    [TestMethod]
    public void RedactStreamUrl_NotAUrl_ReturnsNull()
        => Assert.IsNull(ProviderApiEndpoints.RedactStreamUrl("not-a-url"));

    [TestMethod]
    public void RedactStreamUrl_StripsQueryString()
    {
        var result = ProviderApiEndpoints.RedactStreamUrl("http://example.com/live/stream?user=alice&pass=secret");
        Assert.AreEqual("http://example.com/live/stream", result);
    }

    [TestMethod]
    public void RedactStreamUrl_StripsCredentials()
    {
        var result = ProviderApiEndpoints.RedactStreamUrl("http://user:password@example.com/stream");
        Assert.AreEqual("http://example.com/stream", result);
    }

    [TestMethod]
    public void RedactStreamUrl_StripsCredentialsAndQueryString()
    {
        var result = ProviderApiEndpoints.RedactStreamUrl("http://admin:secret@example.com/live?token=abc&output=ts");
        Assert.AreEqual("http://example.com/live", result);
    }

    [TestMethod]
    public void RedactStreamUrl_PlainUrl_ReturnedUnchanged()
    {
        var result = ProviderApiEndpoints.RedactStreamUrl("http://example.com/stream/channel1");
        Assert.AreEqual("http://example.com/stream/channel1", result);
    }

    // -------------------------------------------------------------------------
    // Test helpers
    // -------------------------------------------------------------------------

    private static async Task SeedChannelsAsync(
        ApplicationDbContext db,
        string providerId,
        string fetchRunId,
        (string DisplayName, string? GroupTitle, string StreamUrl)[] channels)
    {
        var now = DateTime.UtcNow;
        var provider = new Provider
        {
            ProviderId = providerId,
            Name = providerId,
            Enabled = true,
            PlaylistUrl = "http://example.com/playlist.m3u",
            TimeoutSeconds = 20,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        var fetchRun = new FetchRun
        {
            FetchRunId = fetchRunId,
            ProviderId = providerId,
            StartedUtc = now,
            Status = "ok",
        };

        db.Providers.Add(provider);
        db.FetchRuns.Add(fetchRun);
        await db.SaveChangesAsync();

        var parsedChannels = channels.Select(c => new ParsedProviderChannel
        {
            DisplayName = c.DisplayName,
            StreamUrl = c.StreamUrl,
            GroupTitle = c.GroupTitle,
        }).ToList();

        await ProviderApiEndpoints.UpsertProviderGroupsAsync(db, providerId, parsedChannels, now, CancellationToken.None);
        await ProviderApiEndpoints.UpsertProviderChannelsAsync(db, providerId, fetchRunId, parsedChannels, now, CancellationToken.None);
        await db.SaveChangesAsync();
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
        return fixture;
    }

    private sealed class TestFixture(SqliteConnection connection, DbContextOptions<ApplicationDbContext> options) : IAsyncDisposable
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}

