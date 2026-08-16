using M3Undle.Web.Application;
using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class ChannelMappingPageServiceTests
{
    [TestMethod]
    public async Task UpdateChannelSelectionsAsync_SelectingChannelInNewManualReviewGroup_KeepsGroupIncluded()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedBasicMappingGraphAsync(fixture);

        var service = new ChannelMappingPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new TestRefreshTrigger(),
            new AppEventBus());

        var updated = await service.UpdateChannelSelectionsAsync(
            "profile-1",
            "filter-1",
            new UpdateChannelSelectionsRequest
            {
                ChannelMode = LineupReviewSemantics.GroupModeManualReview,
                Channels =
                [
                    new ChannelSelectionItem
                    {
                        ProviderChannelId = "channel-1",
                        ChannelNumber = 101,
                    }
                ],
            },
            CancellationToken.None);

        Assert.IsTrue(updated);

        await using var verifyScope = fixture.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var filter = await verifyDb.ProfileGroupFilters.SingleAsync(x => x.ProfileGroupFilterId == "filter-1");
        Assert.AreEqual(LineupReviewSemantics.GroupDecisionInclude, filter.Decision);
        Assert.IsFalse(filter.IsNew);

        var row = await verifyDb.ProfileGroupChannelFilters.SingleAsync(x => x.ProfileGroupFilterId == "filter-1");
        Assert.AreEqual("channel-1", row.ProviderChannelId);
        Assert.AreEqual(LineupReviewSemantics.ChannelStateIncluded, row.State);
        Assert.AreEqual(101, row.ChannelNumber);
    }

    [TestMethod]
    public async Task UpdateChannelSelectionsAsync_ManualSelectionSave_PreservesPendingAndExcludedRows()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedBasicMappingGraphAsync(fixture);

        await using (var seedScope = fixture.Services.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            db.ProfileGroupChannelFilters.AddRange(
                NewChannelFilterRow("row-pending", "filter-1", "channel-2", LineupReviewSemantics.ChannelStatePending),
                NewChannelFilterRow("row-excluded", "filter-1", "channel-3", LineupReviewSemantics.ChannelStateExcluded),
                NewChannelFilterRow("row-included", "filter-1", "channel-4", LineupReviewSemantics.ChannelStateIncluded));

            await db.SaveChangesAsync();
        }

        var service = new ChannelMappingPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new TestRefreshTrigger(),
            new AppEventBus());

        var updated = await service.UpdateChannelSelectionsAsync(
            "profile-1",
            "filter-1",
            new UpdateChannelSelectionsRequest
            {
                ChannelMode = LineupReviewSemantics.GroupModeManualReview,
                Channels =
                [
                    new ChannelSelectionItem
                    {
                        ProviderChannelId = "channel-1",
                        ChannelNumber = 201,
                    }
                ],
            },
            CancellationToken.None);

        Assert.IsTrue(updated);

        await using var verifyScope = fixture.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var filter = await verifyDb.ProfileGroupFilters.SingleAsync(x => x.ProfileGroupFilterId == "filter-1");
        Assert.IsFalse(filter.IsNew);

        var rows = await verifyDb.ProfileGroupChannelFilters
            .Where(x => x.ProfileGroupFilterId == "filter-1")
            .OrderBy(x => x.ProviderChannelId)
            .ToListAsync();

        Assert.HasCount(3, rows);
        Assert.AreEqual(LineupReviewSemantics.ChannelStateIncluded, rows.Single(x => x.ProviderChannelId == "channel-1").State);
        Assert.AreEqual(LineupReviewSemantics.ChannelStatePending, rows.Single(x => x.ProviderChannelId == "channel-2").State);
        Assert.AreEqual(LineupReviewSemantics.ChannelStateExcluded, rows.Single(x => x.ProviderChannelId == "channel-3").State);
        Assert.IsFalse(rows.Any(x => x.ProviderChannelId == "channel-4"));
    }

    [TestMethod]
    public async Task UpdateGroupFilterAsync_StateChangingEdit_ClearsIsNew()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedBasicMappingGraphAsync(fixture);

        var service = new ChannelMappingPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new TestRefreshTrigger(),
            new AppEventBus());

        var updated = await service.UpdateGroupFilterAsync(
            "profile-1",
            "filter-1",
            new UpdateGroupFilterRequest
            {
                Decision = LineupReviewSemantics.GroupDecisionExclude,
            },
            CancellationToken.None);

        Assert.IsTrue(updated);

        await using var verifyScope = fixture.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var filter = await verifyDb.ProfileGroupFilters.SingleAsync(x => x.ProfileGroupFilterId == "filter-1");
        Assert.AreEqual(LineupReviewSemantics.GroupDecisionExclude, filter.Decision);
        Assert.IsFalse(filter.IsNew);
    }

    [TestMethod]
    public async Task ListGroupFiltersAsync_OnlyCountsIncludedChannels()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedBasicMappingGraphAsync(fixture);

        await using (var seedScope = fixture.Services.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ProfileGroupChannelFilters.AddRange(
                NewChannelFilterRow("r1", "filter-1", "channel-1", LineupReviewSemantics.ChannelStateIncluded),
                NewChannelFilterRow("r2", "filter-1", "channel-2", LineupReviewSemantics.ChannelStatePending),
                NewChannelFilterRow("r3", "filter-1", "channel-3", LineupReviewSemantics.ChannelStateExcluded));
            await db.SaveChangesAsync();
        }

        var service = new ChannelMappingPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new TestRefreshTrigger(),
            new AppEventBus());

        var filters = await service.ListGroupFiltersAsync("profile-1", CancellationToken.None);
        var dto = filters.Single(f => f.ProfileGroupFilterId == "filter-1");

        Assert.AreEqual(1, dto.SelectedChannelCount);
    }

    [TestMethod]
    public async Task ListGroupFiltersAsync_EmptyGroupPastGracePeriod_FlagsIsEmptyStale()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedBasicMappingGraphAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var group = await db.ProviderGroups.SingleAsync(x => x.ProviderGroupId == "group-1");
            group.Active = false;
            group.ChannelCount = 0;
            group.LastSeenUtc = DateTime.UtcNow.AddDays(-(LineupReviewSemantics.EmptyGroupGraceDays + 1));
            await db.SaveChangesAsync();
        }

        var service = new ChannelMappingPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new TestRefreshTrigger(),
            new AppEventBus());

        var filters = await service.ListGroupFiltersAsync("profile-1", CancellationToken.None);
        var dto = filters.Single(f => f.ProfileGroupFilterId == "filter-1");

        Assert.IsTrue(dto.IsEmptyStale);
    }

    [TestMethod]
    public async Task ListGroupFiltersAsync_EmptyGroupWithinGracePeriod_DoesNotFlagIsEmptyStale()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedBasicMappingGraphAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var group = await db.ProviderGroups.SingleAsync(x => x.ProviderGroupId == "group-1");
            group.Active = false;
            group.ChannelCount = 0;
            group.LastSeenUtc = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        var service = new ChannelMappingPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new TestRefreshTrigger(),
            new AppEventBus());

        var filters = await service.ListGroupFiltersAsync("profile-1", CancellationToken.None);
        var dto = filters.Single(f => f.ProfileGroupFilterId == "filter-1");

        Assert.IsFalse(dto.IsEmptyStale, "A recently-vanished group is within the grace period and must not be flagged yet.");
    }

    [TestMethod]
    public async Task RemoveEmptyGroupAsync_StaleEmptyGroup_DeletesGroupAndCascadesFilter()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedBasicMappingGraphAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var group = await db.ProviderGroups.SingleAsync(x => x.ProviderGroupId == "group-1");
            group.Active = false;
            group.ChannelCount = 0;
            group.LastSeenUtc = DateTime.UtcNow.AddDays(-(LineupReviewSemantics.EmptyGroupGraceDays + 1));
            await db.SaveChangesAsync();
        }

        var service = new ChannelMappingPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new TestRefreshTrigger(),
            new AppEventBus());

        var removed = await service.RemoveEmptyGroupAsync("profile-1", "filter-1", CancellationToken.None);
        Assert.IsTrue(removed);

        await using var verifyScope = fixture.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.IsFalse(await verifyDb.ProviderGroups.AnyAsync(x => x.ProviderGroupId == "group-1"));
        Assert.IsFalse(await verifyDb.ProfileGroupFilters.AnyAsync(x => x.ProfileGroupFilterId == "filter-1"),
            "Deleting the provider group must cascade-delete its profile group filter.");
    }

    [TestMethod]
    public async Task RemoveEmptyGroupAsync_GroupNotYetPastGracePeriod_DoesNotDelete()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedBasicMappingGraphAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var group = await db.ProviderGroups.SingleAsync(x => x.ProviderGroupId == "group-1");
            group.Active = false;
            group.ChannelCount = 0;
            group.LastSeenUtc = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        var service = new ChannelMappingPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new TestRefreshTrigger(),
            new AppEventBus());

        var removed = await service.RemoveEmptyGroupAsync("profile-1", "filter-1", CancellationToken.None);
        Assert.IsFalse(removed, "A group still inside its grace period must not be removable yet.");

        await using var verifyScope = fixture.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.IsTrue(await verifyDb.ProviderGroups.AnyAsync(x => x.ProviderGroupId == "group-1"));
    }

    private static async Task SeedBasicMappingGraphAsync(TestFixture fixture)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;

        db.Profiles.Add(new Profile
        {
            ProfileId = "profile-1",
            Name = "Profile 1",
            Enabled = true,
            IsActive = true,
            OutputName = "profile-1",
            MergeMode = "replace",
            CreatedUtc = now,
            UpdatedUtc = now,
        });

        db.Providers.Add(new Provider
        {
            ProviderId = "provider-1",
            Name = "Provider 1",
            Enabled = true,
            PlaylistUrl = "http://example.com/playlist.m3u",
            CreatedUtc = now,
            UpdatedUtc = now,
        });

        db.FetchRuns.Add(new FetchRun
        {
            FetchRunId = "fetch-1",
            ProviderId = "provider-1",
            StartedUtc = now,
            FinishedUtc = now,
            Status = "succeeded",
            Type = "snapshot",
            ChannelCountSeen = 4,
        });

        db.ProviderGroups.Add(new ProviderGroup
        {
            ProviderGroupId = "group-1",
            ProviderId = "provider-1",
            RawName = "USA LOCALS",
            FirstSeenUtc = now,
            LastSeenUtc = now,
            Active = true,
            ChannelCount = 4,
            ContentType = "live",
        });

        db.ProfileGroupFilters.Add(new ProfileGroupFilter
        {
            ProfileGroupFilterId = "filter-1",
            ProfileId = "profile-1",
            ProviderGroupId = "group-1",
            Decision = LineupReviewSemantics.GroupDecisionInclude,
            IsNew = true,
            ChannelMode = LineupReviewSemantics.GroupModeManualReview,
            TrackingPolicy = LineupReviewSemantics.TrackingPolicyReview,
            TrackNewChannels = false,
            CreatedUtc = now,
            UpdatedUtc = now,
        });

        db.ProviderChannels.AddRange(
            NewProviderChannel("channel-1", "NY | ABC", now),
            NewProviderChannel("channel-2", "NY | CBS", now),
            NewProviderChannel("channel-3", "NY | FOX", now),
            NewProviderChannel("channel-4", "NY | NBC", now));

        await db.SaveChangesAsync();
    }

    private static ProviderChannel NewProviderChannel(string id, string name, DateTime now)
        => new()
        {
            ProviderChannelId = id,
            ProviderId = "provider-1",
            ProviderChannelKey = id,
            DisplayName = name,
            StreamUrl = $"http://example.com/{id}",
            GroupTitle = "USA LOCALS",
            ProviderGroupId = "group-1",
            IsEvent = false,
            IsPlaceholder = false,
            FirstSeenUtc = now,
            LastSeenUtc = now,
            Active = true,
            ContentType = "live",
            LastFetchRunId = "fetch-1",
        };

    private static ProfileGroupChannelFilter NewChannelFilterRow(string id, string filterId, string channelId, string state)
    {
        var now = DateTime.UtcNow;
        return new ProfileGroupChannelFilter
        {
            ProfileGroupChannelFilterId = id,
            ProfileGroupFilterId = filterId,
            ProviderChannelId = channelId,
            State = state,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));

        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        return new TestFixture(connection, provider);
    }

    private sealed class TestFixture(SqliteConnection connection, ServiceProvider services) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;
        public ServiceProvider Services { get; } = services;

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class TestRefreshTrigger : IRefreshTrigger
    {
        public bool IsRefreshing => false;
        public DateTime? RefreshStartedAt => null;
        public string? CurrentActivity => null;
        public bool TriggerRefresh() => true;
        public bool TriggerBuildOnly() => true;
        public void CancelRefresh() { }
    }
}
