using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Persistence;

[TestClass]
public sealed class SchemaConstraintTests
{
    [TestMethod]
    public async Task CanonicalChannelNumber_IsUniquePerProfile()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        var profile = NewProfile("profile-1");
        db.Profiles.Add(profile);

        db.CanonicalChannels.Add(NewCanonical("channel-1", profile.ProfileId, 100));
        db.CanonicalChannels.Add(NewCanonical("channel-2", profile.ProfileId, 100));

        await AssertDbUpdateExceptionAsync(() => db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task ProviderChannelKey_PartialUnique_AllowsNulls_RejectsDuplicateNonNull()
    {
        await using var fixture = await CreateFixtureAsync();

        var provider = NewProvider("provider-1");
        var fetch = NewFetchRun("fetch-1", provider.ProviderId);

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(provider);
            setup.FetchRuns.Add(fetch);
            await setup.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            db.ProviderChannels.Add(NewProviderChannel("pc-1", provider.ProviderId, fetch.FetchRunId, null));
            db.ProviderChannels.Add(NewProviderChannel("pc-2", provider.ProviderId, fetch.FetchRunId, "   "));
            db.ProviderChannels.Add(NewProviderChannel("pc-3", provider.ProviderId, fetch.FetchRunId, "stable-key"));
            await db.SaveChangesAsync();
        }

        await using (var verify = fixture.CreateDbContext())
        {
            var stored = await verify.ProviderChannels
                .OrderBy(x => x.ProviderChannelId)
                .ToListAsync();

            Assert.IsNull(stored.Single(x => x.ProviderChannelId == "pc-1").ProviderChannelKey);
            Assert.IsNull(stored.Single(x => x.ProviderChannelId == "pc-2").ProviderChannelKey);
            Assert.AreEqual("stable-key", stored.Single(x => x.ProviderChannelId == "pc-3").ProviderChannelKey);
        }

        await using (var conflict = fixture.CreateDbContext())
        {
            conflict.ProviderChannels.Add(NewProviderChannel("pc-4", provider.ProviderId, fetch.FetchRunId, "stable-key"));
            await AssertDbUpdateExceptionAsync(() => conflict.SaveChangesAsync());
        }
    }

    [TestMethod]
    public async Task DeletingProviderGroup_SetsProviderChannelGroupIdToNull()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        var provider = NewProvider("provider-1");
        var fetch = NewFetchRun("fetch-1", provider.ProviderId);
        var group = NewProviderGroup("group-1", provider.ProviderId, "Sports");
        var channel = NewProviderChannel("pc-1", provider.ProviderId, fetch.FetchRunId, "key-1");
        channel.ProviderGroupId = group.ProviderGroupId;

        db.Providers.Add(provider);
        db.FetchRuns.Add(fetch);
        db.ProviderGroups.Add(group);
        db.ProviderChannels.Add(channel);
        await db.SaveChangesAsync();

        db.ProviderGroups.Remove(group);
        await db.SaveChangesAsync();

        var reloaded = await db.ProviderChannels.SingleAsync(x => x.ProviderChannelId == channel.ProviderChannelId);
        Assert.IsNull(reloaded.ProviderGroupId);
    }

    [TestMethod]
    public async Task ProfileDelete_IsRestricted_WhenCanonicalChannelsExist()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        var profile = NewProfile("profile-1");
        db.Profiles.Add(profile);
        db.CanonicalChannels.Add(NewCanonical("channel-1", profile.ProfileId, 101));
        await db.SaveChangesAsync();

        db.Profiles.Remove(profile);
        await AssertDbUpdateExceptionAsync(() => db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task CanonicalDelete_CascadesToStreamKeys_AndEpgMaps_AndChannelSources()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        var profile = NewProfile("profile-1");
        var provider = NewProvider("provider-1");
        var fetch = NewFetchRun("fetch-1", provider.ProviderId);
        var canonical = NewCanonical("channel-1", profile.ProfileId, 101);
        var providerChannel = NewProviderChannel("pc-1", provider.ProviderId, fetch.FetchRunId, "key-1");

        db.Profiles.Add(profile);
        db.Providers.Add(provider);
        db.FetchRuns.Add(fetch);
        db.CanonicalChannels.Add(canonical);
        db.ProviderChannels.Add(providerChannel);
        await db.SaveChangesAsync();

        db.StreamKeys.Add(new StreamKey
        {
            Value = "sk-1",
            ProfileId = profile.ProfileId,
            ChannelId = canonical.ChannelId,
            CreatedUtc = DateTime.UtcNow,
            Revoked = false,
        });

        db.EpgChannelMaps.Add(new EpgChannelMap
        {
            EpgMapId = "epg-1",
            ProfileId = profile.ProfileId,
            ChannelId = canonical.ChannelId,
            XmltvChannelId = "xmltv-1",
            Source = "manual",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });

        db.ChannelSources.Add(new ChannelSource
        {
            ChannelSourceId = "cs-1",
            ChannelId = canonical.ChannelId,
            ProviderId = provider.ProviderId,
            ProviderChannelId = providerChannel.ProviderChannelId,
            Priority = 1,
            Enabled = true,
            HealthState = "unknown",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();

        db.CanonicalChannels.Remove(canonical);
        await db.SaveChangesAsync();

        Assert.AreEqual(0, await db.StreamKeys.CountAsync());
        Assert.AreEqual(0, await db.EpgChannelMaps.CountAsync());
        Assert.AreEqual(0, await db.ChannelSources.CountAsync());
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

    private static async Task AssertDbUpdateExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            Assert.Fail("Expected DbUpdateException to be thrown.");
        }
        catch (DbUpdateException)
        {
            // Expected path.
        }
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

    private static Provider NewProvider(string id) => new()
    {
        ProviderId = id,
        Name = id,
        Enabled = true,
        PlaylistUrl = "http://example.com/playlist.m3u",
        TimeoutSeconds = 20,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static FetchRun NewFetchRun(string id, string providerId) => new()
    {
        FetchRunId = id,
        ProviderId = providerId,
        StartedUtc = DateTime.UtcNow,
        Status = "ok",
    };

    private static ProviderGroup NewProviderGroup(string id, string providerId, string rawName) => new()
    {
        ProviderGroupId = id,
        ProviderId = providerId,
        RawName = rawName,
        FirstSeenUtc = DateTime.UtcNow,
        LastSeenUtc = DateTime.UtcNow,
        Active = true,
    };

    private static CanonicalChannel NewCanonical(string id, string profileId, int number) => new()
    {
        ChannelId = id,
        ProfileId = profileId,
        DisplayName = id,
        ChannelNumber = number,
        Enabled = true,
        IsEvent = false,
        EventPolicy = "manual",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static ProviderChannel NewProviderChannel(string id, string providerId, string fetchRunId, string? key) => new()
    {
        ProviderChannelId = id,
        ProviderId = providerId,
        ProviderChannelKey = key,
        DisplayName = id,
        StreamUrl = "http://example.com/stream",
        IsEvent = false,
        FirstSeenUtc = DateTime.UtcNow,
        LastSeenUtc = DateTime.UtcNow,
        Active = true,
        LastFetchRunId = fetchRunId,
    };

    private sealed class TestFixture(SqliteConnection connection, DbContextOptions<ApplicationDbContext> options) : IAsyncDisposable
    {
        public ApplicationDbContext CreateDbContext() => new(options);

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}

