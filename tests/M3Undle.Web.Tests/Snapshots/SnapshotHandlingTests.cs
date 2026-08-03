using M3Undle.Core.M3u;
using M3Undle.Web.Api;
using M3Undle.Web.Application;
using M3Undle.Web.Tests.Stubs;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
            setup.Providers.Add(NewProvider("provider-1"));
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
    public async Task SnapshotBuilder_FetchFailureEventPublishFails_StillReturnsFailedRefresh()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            setup.Providers.Add(NewProvider("provider-1"));
            setup.ProfileProviders.Add(NewProfileProvider("provider-1", "profile-1"));
            await setup.SaveChangesAsync();
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using var db = fixture.CreateDbContext();
            var builder = CreateBuilder(
                db,
                HttpStatusCode.InternalServerError,
                "upstream error",
                tempDir,
                new ThrowingEventService());

            var result = await builder.RunAsync(CancellationToken.None);

            await using var verify = fixture.CreateDbContext();
            var fetchRun = await verify.FetchRuns.SingleAsync();
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("fail", fetchRun.Status);
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
            setup.Providers.Add(NewProvider("provider-1"));
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

            var xmlBytes = await File.ReadAllBytesAsync(snapshot.XmltvPath);
            Assert.IsNotEmpty(xmlBytes);
            Assert.IsFalse(
                xmlBytes.Length >= 3 && xmlBytes[0] == 0xEF && xmlBytes[1] == 0xBB && xmlBytes[2] == 0xBF,
                "Snapshot XMLTV should be UTF-8 without BOM.");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_InitialProviderSync_BaselinesReviewState()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            setup.Providers.Add(NewProvider("provider-1"));
            setup.ProfileProviders.Add(NewProfileProvider("provider-1", "profile-1"));
            await setup.SaveChangesAsync();
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using (var db = fixture.CreateDbContext())
            {
                await CreateBuilder(db, HttpStatusCode.OK, SampleM3u, tempDir).RunAsync(CancellationToken.None);
            }

            await using var verify = fixture.CreateDbContext();
            var filter = await verify.ProfileGroupFilters
                .Include(x => x.ProviderGroup)
                .SingleAsync(x => x.ProfileId == "profile-1" && x.ProviderGroup.RawName == "News");

            Assert.IsFalse(filter.IsNew);
            Assert.AreEqual(LineupReviewSemantics.GroupModeManualReview, filter.ChannelMode);
            Assert.AreEqual(LineupReviewSemantics.TrackingPolicyReview, filter.TrackingPolicy);
            Assert.IsFalse(filter.TrackNewChannels, "TrackNewChannels should default to false so no channel rows are created until user opts in.");

            // With TrackNewChannels = false (default), no channel rows should be created.
            var rows = await verify.ProfileGroupChannelFilters
                .Where(x => x.ProfileGroupFilterId == filter.ProfileGroupFilterId)
                .ToListAsync();

            Assert.HasCount(0, rows);
            Assert.AreEqual(0, await verify.ProfileGroupChannelFilters
                .CountAsync(x => x.State == LineupReviewSemantics.ChannelStatePending));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_SubsequentRefresh_QueuesOnlyNewGroupsAndChannels()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            setup.Providers.Add(NewProvider("provider-1"));
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
                await CreateBuilder(db2, HttpStatusCode.OK, SampleNewGroupAndChannelM3u, tempDir).RunAsync(CancellationToken.None);
            }

            await using var verify = fixture.CreateDbContext();
            var filters = await verify.ProfileGroupFilters
                .Include(x => x.ProviderGroup)
                .Where(x => x.ProfileId == "profile-1")
                .ToListAsync();

            Assert.IsFalse(filters.Single(x => x.ProviderGroup.RawName == "News").IsNew);
            Assert.IsTrue(filters.Single(x => x.ProviderGroup.RawName == "Sports").IsNew);

            // With TrackNewChannels = false (default on all groups), no channel rows are queued.
            var rows = await verify.ProfileGroupChannelFilters.ToListAsync();
            Assert.HasCount(0, rows);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_RefreshesActiveProfileProvidersBeforeStandbyProviders()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            var activeProfile = NewProfile("profile-active");
            activeProfile.IsActive = true;

            var standbyProvider = NewProvider("provider-standby");
            standbyProvider.Name = "aaa-standby";
            standbyProvider.PlaylistUrl = "http://standby.example/playlist.m3u";
            standbyProvider.XmltvUrl = "http://standby.example/xmltv.xml";

            var activeProvider = NewProvider("provider-active");
            activeProvider.Name = "zzz-active";
            activeProvider.PlaylistUrl = "http://active.example/playlist.m3u";
            activeProvider.XmltvUrl = "http://active.example/xmltv.xml";

            var unlinkedProvider = NewProvider("provider-unlinked");
            unlinkedProvider.Name = "aaa-unlinked";
            unlinkedProvider.PlaylistUrl = "http://unlinked.example/playlist.m3u";
            unlinkedProvider.XmltvUrl = "http://unlinked.example/xmltv.xml";

            setup.Profiles.AddRange(
                activeProfile,
                NewProfile("profile-standby"));
            setup.Providers.AddRange(
                standbyProvider,
                activeProvider,
                unlinkedProvider);
            setup.ProfileProviders.AddRange(
                NewProfileProvider("provider-standby", "profile-standby"),
                NewProfileProvider("provider-active", "profile-active"));
            await setup.SaveChangesAsync();
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var handler = new TrackingHttpMessageHandler();
            await using var db = fixture.CreateDbContext();
            var builder = CreateBuilder(db, HttpStatusCode.OK, SampleM3u, tempDir, handler: handler);
            await builder.RunAsync(CancellationToken.None);

            CollectionAssert.AreEqual(
                new[] { "active.example", "standby.example" },
                handler.PlaylistRequestHosts);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_ProviderBackOnline_PublishesOnlyOnceForCurrentFailure()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            setup.Providers.Add(NewProvider("provider-1"));
            setup.ProfileProviders.Add(NewProfileProvider("provider-1", "profile-1"));
            await setup.SaveChangesAsync();
        }

        var eventService = new RecoveryTrackingEventService();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using (var db1 = fixture.CreateDbContext())
            {
                await CreateBuilder(db1, HttpStatusCode.OK, SampleM3u, tempDir, eventService)
                    .RunAsync(CancellationToken.None);
            }

            await using (var db2 = fixture.CreateDbContext())
            {
                await CreateBuilder(db2, HttpStatusCode.OK, SampleM3u, tempDir, eventService)
                    .RunAsync(CancellationToken.None);
            }

            Assert.AreEqual(1, eventService.ProviderBackOnlinePublishCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_SecondIdenticalFetch_DoesNotCreateNewSnapshot()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            setup.Providers.Add(NewProvider("provider-1"));
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
            Assert.HasCount(1, snapshots);
            Assert.AreEqual("active", snapshots[0].Status);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_SecondChangedFetch_ArchivesPreviousSnapshot()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            setup.Providers.Add(NewProvider("provider-1"));
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

            await using (var configure = fixture.CreateDbContext())
            {
                var group = await configure.ProviderGroups.SingleAsync(x => x.ProviderId == "provider-1" && x.RawName == "News");
                var filter = await configure.ProfileGroupFilters
                    .SingleAsync(x => x.ProfileId == "profile-1" && x.ProviderGroupId == group.ProviderGroupId);

                filter.Decision = LineupReviewSemantics.GroupDecisionInclude;
                filter.OutputName = "News";
                filter.ChannelMode = LineupReviewSemantics.GroupModeAutoUpdate;
                filter.TrackingPolicy = LineupReviewSemantics.TrackingPolicyAutoAddAll;
                filter.TrackNewChannels = false;
                filter.UpdatedUtc = DateTime.UtcNow;

                var channelRows = await configure.ProfileGroupChannelFilters
                    .Where(x => x.ProfileGroupFilterId == filter.ProfileGroupFilterId)
                    .ToListAsync();

                foreach (var channelRow in channelRows)
                {
                    channelRow.State = LineupReviewSemantics.ChannelStateIncluded;
                    channelRow.UpdatedUtc = DateTime.UtcNow;
                }

                await configure.SaveChangesAsync();
            }

            await using (var db2 = fixture.CreateDbContext())
            {
                await CreateBuilder(db2, HttpStatusCode.OK, SampleUpdatedM3u, tempDir).RunAsync(CancellationToken.None);
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
    public async Task SnapshotBuilder_IgnoresDormantCatalogGroupExclusion_WhenVodAndSeriesEnabled()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            var provider = NewProvider("provider-1");
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

                var catalogItems = await verify1.CatalogItems
                    .AsNoTracking()
                    .OrderBy(x => x.ContentType)
                    .ToListAsync();
                Assert.HasCount(2, catalogItems);
                Assert.IsTrue(catalogItems.All(x => !x.ProviderItemKey.Contains("user/pass", StringComparison.Ordinal)));
                Assert.AreEqual(1, catalogItems.Single(x => x.ContentType == "series").EpisodeCount);
            }

            await using (var edit = fixture.CreateDbContext())
            {
                var newsFilter = await edit.ProfileGroupFilters
                    .Include(x => x.ProviderGroup)
                    .SingleAsync(x => x.ProfileId == "profile-1" && x.ProviderGroup.RawName == "News");
                newsFilter.Decision = LineupReviewSemantics.GroupDecisionInclude;
                newsFilter.IsNew = false;
                newsFilter.UpdatedUtc = DateTime.UtcNow;

                // Catalog decisions use their own slim rows rather than the live mapping table.
                var catalogFilterCount = await edit.ProfileGroupFilters
                    .CountAsync(x => x.ProfileId == "profile-1"
                                  && (x.ProviderGroup.RawName == "Movies" || x.ProviderGroup.RawName == "Series"));
                Assert.AreEqual(0, catalogFilterCount, "VOD/series groups should not receive group filters.");

                var seriesGroup = await edit.ProviderGroups.SingleAsync(x =>
                    x.ProviderId == "provider-1"
                    && x.RawName == "Series"
                    && x.ContentType == "series");
                edit.ProfileCatalogGroupFilters.Add(new ProfileCatalogGroupFilter
                {
                    ProfileCatalogGroupFilterId = Guid.NewGuid().ToString(),
                    ProfileId = "profile-1",
                    ProviderGroupId = seriesGroup.ProviderGroupId,
                    Decision = LineupReviewSemantics.GroupDecisionExclude,
                    IsNew = false,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                });

                // Since TrackNewChannels = false by default, no channel rows were auto-created.
                // Directly insert included rows for the live news channels.
                var nowEdit = DateTime.UtcNow;
                var newsChannelIds = await edit.ProviderChannels
                    .AsNoTracking()
                    .Where(x => x.ProviderGroupId == newsFilter.ProviderGroupId && x.Active && x.ContentType == "live")
                    .Select(x => x.ProviderChannelId)
                    .ToListAsync();
                foreach (var channelId in newsChannelIds)
                {
                    edit.ProfileGroupChannelFilters.Add(new ProfileGroupChannelFilter
                    {
                        ProfileGroupChannelFilterId = Guid.NewGuid().ToString(),
                        ProfileGroupFilterId = newsFilter.ProfileGroupFilterId,
                        ProviderChannelId = channelId,
                        State = LineupReviewSemantics.ChannelStateIncluded,
                        CreatedUtc = nowEdit,
                        UpdatedUtc = nowEdit,
                    });
                }

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
            Assert.AreEqual(3, active2.ChannelCountPublished,
                "Dormant catalog decisions must not silently exclude content after their UI is hidden.");

            await using (var db3 = fixture.CreateDbContext())
            {
                await CreateBuilder(db3, HttpStatusCode.OK, SampleM3u, tempDir).RunAsync(CancellationToken.None);
            }

            await using var verify3 = fixture.CreateDbContext();
            Assert.AreEqual(0, await verify3.CatalogItems.CountAsync(x => x.Active),
                "A successful refresh that no longer returns catalog items must deactivate stale browse rows.");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_SplitsSameNamedGroups_ByContentType()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            var provider = NewProvider("provider-1");
            provider.IncludeVod = true;
            provider.IncludeSeries = true;
            setup.Providers.Add(provider);
            setup.ProfileProviders.Add(NewProfileProvider("provider-1", "profile-1"));
            await setup.SaveChangesAsync();
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using (var db = fixture.CreateDbContext())
            {
                await CreateBuilder(db, HttpStatusCode.OK, SampleSameNameAcrossContentTypesM3u, tempDir).RunAsync(CancellationToken.None);
            }

            await using var verify = fixture.CreateDbContext();

            // One "Comedy" category per content type, not a single collapsed row.
            var comedyGroups = await verify.ProviderGroups
                .AsNoTracking()
                .Where(x => x.ProviderId == "provider-1" && x.RawName == "Comedy")
                .OrderBy(x => x.ContentType)
                .ToListAsync();

            CollectionAssert.AreEqual(
                new[] { "live", "series", "vod" },
                comedyGroups.Select(x => x.ContentType).ToArray(),
                "Same-named live/VOD/series categories must be distinct group rows.");

            // Each row counts only its own content type — no summing across kinds.
            foreach (var group in comedyGroups)
                Assert.AreEqual(1, group.ChannelCount, $"Comedy/{group.ContentType} should count only its own items.");

            // Only the live row is mappable, so only it receives a filter row. A user excluding
            // live "Comedy" must not be silently deciding anything about catalog "Comedy".
            var filters = await verify.ProfileGroupFilters
                .AsNoTracking()
                .Include(x => x.ProviderGroup)
                .Where(x => x.ProfileId == "profile-1" && x.ProviderGroup.RawName == "Comedy")
                .ToListAsync();

            Assert.AreEqual(1, filters.Count, "Only the live Comedy group should be mappable.");
            Assert.AreEqual("live", filters[0].ProviderGroup.ContentType);

            // The persisted live channel must attach to the live row, never a catalog row.
            var liveGroupId = comedyGroups.Single(x => x.ContentType == "live").ProviderGroupId;
            var persisted = await verify.ProviderChannels
                .AsNoTracking()
                .Where(x => x.ProviderId == "provider-1")
                .ToListAsync();

            Assert.AreEqual(1, persisted.Count, "Only live channels are persisted.");
            Assert.AreEqual(liveGroupId, persisted[0].ProviderGroupId);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_UpgradeReusesLegacyMixedGroupAndPreservesFilter()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            var provider = NewProvider("provider-1");
            provider.IncludeVod = true;
            provider.IncludeSeries = true;
            setup.Providers.Add(provider);
            setup.ProfileProviders.Add(NewProfileProvider("provider-1", "profile-1"));
            setup.ProviderGroups.Add(new ProviderGroup
            {
                ProviderGroupId = "legacy-comedy",
                ProviderId = "provider-1",
                RawName = "Comedy",
                ContentType = "mixed",
                ChannelCount = 3,
                Active = true,
                FirstSeenUtc = DateTime.UtcNow.AddDays(-1),
                LastSeenUtc = DateTime.UtcNow.AddDays(-1),
            });
            setup.ProfileGroupFilters.Add(new ProfileGroupFilter
            {
                ProfileGroupFilterId = "legacy-comedy-filter",
                ProfileId = "profile-1",
                ProviderGroupId = "legacy-comedy",
                Decision = "exclude",
                ChannelMode = "all",
                TrackingPolicy = "review",
                OutputName = "My Comedy",
                CreatedUtc = DateTime.UtcNow.AddDays(-1),
                UpdatedUtc = DateTime.UtcNow.AddDays(-1),
            });
            await setup.SaveChangesAsync();
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using (var db = fixture.CreateDbContext())
            {
                var result = await CreateBuilder(db, HttpStatusCode.OK, SampleSameNameAcrossContentTypesM3u, tempDir)
                    .RunAsync(CancellationToken.None);
                Assert.IsTrue(result.Succeeded, result.ErrorSummary);
            }

            await using var verify = fixture.CreateDbContext();
            var comedyGroups = await verify.ProviderGroups
                .AsNoTracking()
                .Where(x => x.ProviderId == "provider-1" && x.RawName == "Comedy")
                .OrderBy(x => x.ContentType)
                .ToListAsync();

            CollectionAssert.AreEqual(
                new[] { "live", "series", "vod" },
                comedyGroups.Select(x => x.ContentType).ToArray());

            var liveGroup = comedyGroups.Single(x => x.ContentType == "live");
            Assert.AreEqual("legacy-comedy", liveGroup.ProviderGroupId, "The legacy row ID should become the live row.");

            var filters = await verify.ProfileGroupFilters
                .AsNoTracking()
                .Include(x => x.ProviderGroup)
                .Where(x => x.ProfileId == "profile-1" && x.ProviderGroup.RawName == "Comedy")
                .ToListAsync();

            Assert.AreEqual(1, filters.Count, "Upgrade must not create a duplicate same-named live filter.");
            Assert.AreEqual("legacy-comedy-filter", filters[0].ProfileGroupFilterId);
            Assert.AreEqual("exclude", filters[0].Decision);
            Assert.AreEqual("My Comedy", filters[0].OutputName);
            Assert.AreEqual("live", filters[0].ProviderGroup.ContentType);

            var active = await verify.Snapshots
                .AsNoTracking()
                .Where(x => x.Status == "active")
                .OrderByDescending(x => x.CreatedUtc)
                .FirstAsync();
            Assert.AreEqual(0, active.LiveChannelCount, "The preserved exclusion should still remove live Comedy.");
            Assert.AreEqual(1, active.VodChannelCount);
            Assert.AreEqual(1, active.SeriesChannelCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_BuildOnly_HonorsLegacyMixedLiveFilterBeforeRefresh()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            setup.Providers.Add(NewProvider("provider-1"));
            setup.ProfileProviders.Add(NewProfileProvider("provider-1", "profile-1"));
            setup.FetchRuns.Add(NewFetchRun("fetch-1", "provider-1"));
            setup.ProviderGroups.Add(new ProviderGroup
            {
                ProviderGroupId = "legacy-news",
                ProviderId = "provider-1",
                RawName = "News",
                ContentType = "mixed",
                ChannelCount = 1,
                Active = true,
                FirstSeenUtc = DateTime.UtcNow.AddDays(-1),
                LastSeenUtc = DateTime.UtcNow.AddDays(-1),
            });
            setup.ProviderChannels.Add(new ProviderChannel
            {
                ProviderChannelId = "news-channel",
                ProviderId = "provider-1",
                DisplayName = "Live News",
                StreamUrl = "http://example.com/live/user/pass/100.ts",
                GroupTitle = "News",
                ProviderGroupId = "legacy-news",
                Active = true,
                ContentType = "live",
                LastFetchRunId = "fetch-1",
                FirstSeenUtc = DateTime.UtcNow.AddDays(-1),
                LastSeenUtc = DateTime.UtcNow.AddDays(-1),
            });
            setup.ProfileGroupFilters.Add(new ProfileGroupFilter
            {
                ProfileGroupFilterId = "legacy-news-filter",
                ProfileId = "profile-1",
                ProviderGroupId = "legacy-news",
                Decision = "include",
                ChannelMode = "all",
                TrackingPolicy = "review",
                CreatedUtc = DateTime.UtcNow.AddDays(-1),
                UpdatedUtc = DateTime.UtcNow.AddDays(-1),
            });
            await setup.SaveChangesAsync();
        }

        var cachedChannel = new ParsedProviderChannel
        {
            DisplayName = "Live News",
            StreamUrl = "http://example.com/live/user/pass/100.ts",
            GroupTitle = "News",
        };
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using (var db = fixture.CreateDbContext())
            {
                var result = await CreateBuilder(db, HttpStatusCode.OK, "<tv></tv>", tempDir)
                    .BuildOnlyAsync(
                        new Dictionary<string, IReadOnlyList<ParsedProviderChannel>>
                        {
                            ["provider-1"] = [cachedChannel],
                        },
                        CancellationToken.None);
                Assert.IsTrue(result.Succeeded, result.ErrorSummary);
            }

            await using var verify = fixture.CreateDbContext();
            var active = await verify.Snapshots
                .AsNoTracking()
                .Where(x => x.Status == "active")
                .OrderByDescending(x => x.CreatedUtc)
                .FirstAsync();

            Assert.AreEqual(1, active.LiveChannelCount,
                "Build-only must continue treating a legacy mixed group as its live mapping until refresh upgrades it.");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_ExcludingLiveGroup_DoesNotAffectSameNamedCatalogGroup()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            var provider = NewProvider("provider-1");
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
                await CreateBuilder(db1, HttpStatusCode.OK, SampleSameNameAcrossContentTypesM3u, tempDir).RunAsync(CancellationToken.None);
            }

            // Exclude the live "Comedy" group — the catalog items sharing that name must survive.
            await using (var edit = fixture.CreateDbContext())
            {
                var liveComedy = await edit.ProfileGroupFilters
                    .Include(x => x.ProviderGroup)
                    .SingleAsync(x => x.ProfileId == "profile-1"
                                   && x.ProviderGroup.RawName == "Comedy"
                                   && x.ProviderGroup.ContentType == "live");
                liveComedy.Decision = "exclude";
                liveComedy.UpdatedUtc = DateTime.UtcNow;
                await edit.SaveChangesAsync();
            }

            await using (var db2 = fixture.CreateDbContext())
            {
                await CreateBuilder(db2, HttpStatusCode.OK, SampleSameNameAcrossContentTypesM3u, tempDir).RunAsync(CancellationToken.None);
            }

            await using var verify = fixture.CreateDbContext();
            var active = await verify.Snapshots
                .AsNoTracking()
                .Where(x => x.Status == "active")
                .OrderByDescending(x => x.CreatedUtc)
                .FirstAsync();

            Assert.AreEqual(1, active.VodChannelCount, "Excluding live Comedy must not drop VOD Comedy.");
            Assert.AreEqual(1, active.SeriesChannelCount, "Excluding live Comedy must not drop series Comedy.");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_DoesNotCollapseDistinctVodItems_WithSameTvgId()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            var provider = NewProvider("provider-1");
            provider.IncludeVod = true;
            setup.Providers.Add(provider);
            setup.ProfileProviders.Add(NewProfileProvider("provider-1", "profile-1"));
            await setup.SaveChangesAsync();
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using (var db = fixture.CreateDbContext())
            {
                await CreateBuilder(db, HttpStatusCode.OK, SampleDuplicateVodTvgIdM3u, tempDir).RunAsync(CancellationToken.None);
            }

            await using var verify = fixture.CreateDbContext();
            var active = await verify.Snapshots.SingleAsync(x => x.Status == "active");
            Assert.AreEqual(2, active.ChannelCountPublished, "Distinct VOD items with same tvg-id should both be published.");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_PreservesExactDuplicateLiveRows()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            setup.Providers.Add(NewProvider("provider-1"));
            setup.ProfileProviders.Add(NewProfileProvider("provider-1", "profile-1"));
            await setup.SaveChangesAsync();
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            // First run discovers groups/channels and creates pending filters.
            await using (var db1 = fixture.CreateDbContext())
            {
                await CreateBuilder(db1, HttpStatusCode.OK, SampleDuplicateLiveRowsM3u, tempDir).RunAsync(CancellationToken.None);
            }

            // Activate the group and select all its channels for live output.
            await using (var edit = fixture.CreateDbContext())
            {
                var filter = await edit.ProfileGroupFilters
                    .Include(x => x.ProviderGroup)
                    .SingleAsync(x => x.ProfileId == "profile-1" && x.ProviderGroup.RawName == "USA FOX");
                filter.Decision = LineupReviewSemantics.GroupDecisionInclude;
                filter.IsNew = false;
                filter.UpdatedUtc = DateTime.UtcNow;

                // Since TrackNewChannels = false by default, no channel rows were auto-created.
                // Directly insert included rows for the live fox channels.
                var nowEdit = DateTime.UtcNow;
                var foxChannelIds = await edit.ProviderChannels
                    .AsNoTracking()
                    .Where(x => x.ProviderGroupId == filter.ProviderGroupId && x.Active && x.ContentType == "live")
                    .Select(x => x.ProviderChannelId)
                    .ToListAsync();
                foreach (var channelId in foxChannelIds)
                {
                    edit.ProfileGroupChannelFilters.Add(new ProfileGroupChannelFilter
                    {
                        ProfileGroupChannelFilterId = Guid.NewGuid().ToString(),
                        ProfileGroupFilterId = filter.ProfileGroupFilterId,
                        ProviderChannelId = channelId,
                        State = LineupReviewSemantics.ChannelStateIncluded,
                        CreatedUtc = nowEdit,
                        UpdatedUtc = nowEdit,
                    });
                }

                await edit.SaveChangesAsync();
            }

            // Second run should keep both exact-duplicate rows.
            await using (var db2 = fixture.CreateDbContext())
            {
                await CreateBuilder(db2, HttpStatusCode.OK, SampleDuplicateLiveRowsM3u, tempDir).RunAsync(CancellationToken.None);
            }

            await using var verify = fixture.CreateDbContext();
            var active = await verify.Snapshots
                .Where(x => x.Status == "active")
                .OrderByDescending(x => x.CreatedUtc)
                .FirstAsync();
            Assert.AreEqual(2, active.ChannelCountPublished, "Exact duplicate live rows should be preserved to match provider output.");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_PreservesSameChannelAcrossDifferentGroups()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(NewProfile("profile-1"));
            setup.Providers.Add(NewProvider("provider-1"));
            setup.ProfileProviders.Add(NewProfileProvider("provider-1", "profile-1"));
            await setup.SaveChangesAsync();
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using (var db1 = fixture.CreateDbContext())
            {
                await CreateBuilder(db1, HttpStatusCode.OK, SampleSameLiveChannelDifferentGroupsM3u, tempDir).RunAsync(CancellationToken.None);
            }

            await using (var edit = fixture.CreateDbContext())
            {
                var filters = await edit.ProfileGroupFilters
                    .Include(x => x.ProviderGroup)
                    .Where(x => x.ProfileId == "profile-1"
                             && (x.ProviderGroup.RawName == "USA FOX" || x.ProviderGroup.RawName == "USA ABC"))
                    .ToListAsync();
                // Since TrackNewChannels = false by default, no channel rows were auto-created.
                // Directly insert included rows for each group's live channels.
                var nowEdit = DateTime.UtcNow;
                foreach (var filter in filters)
                {
                    filter.Decision = LineupReviewSemantics.GroupDecisionInclude;
                    filter.IsNew = false;
                    filter.UpdatedUtc = nowEdit;

                    var channelIds = await edit.ProviderChannels
                        .AsNoTracking()
                        .Where(x => x.ProviderGroupId == filter.ProviderGroupId && x.Active && x.ContentType == "live")
                        .Select(x => x.ProviderChannelId)
                        .ToListAsync();
                    foreach (var channelId in channelIds)
                    {
                        edit.ProfileGroupChannelFilters.Add(new ProfileGroupChannelFilter
                        {
                            ProfileGroupChannelFilterId = Guid.NewGuid().ToString(),
                            ProfileGroupFilterId = filter.ProfileGroupFilterId,
                            ProviderChannelId = channelId,
                            State = LineupReviewSemantics.ChannelStateIncluded,
                            CreatedUtc = nowEdit,
                            UpdatedUtc = nowEdit,
                        });
                    }
                }

                await edit.SaveChangesAsync();
            }

            await using (var db2 = fixture.CreateDbContext())
            {
                await CreateBuilder(db2, HttpStatusCode.OK, SampleSameLiveChannelDifferentGroupsM3u, tempDir).RunAsync(CancellationToken.None);
            }

            await using var verify = fixture.CreateDbContext();
            var active = await verify.Snapshots
                .Where(x => x.Status == "active")
                .OrderByDescending(x => x.CreatedUtc)
                .FirstAsync();

            Assert.AreEqual(2, active.ChannelCountPublished, "Same channel identity in different groups should be preserved in both groups.");

            // channel_index.ndjson is NDJSON — one JSON object per line, not a JSON array
            var lines = await File.ReadAllLinesAsync(active.ChannelIndexPath);
            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var entries = lines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonSerializer.Deserialize<ChannelIndexEntry>(l, opts)!)
                .ToList();
            Assert.HasCount(2, entries);
            Assert.AreEqual(1, entries.Count(x => x.GroupTitle == "USA FOX"));
            Assert.AreEqual(1, entries.Count(x => x.GroupTitle == "USA ABC"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotBuilder_BuildOnly_EmptyCarriedForwardGuide_RecompilesFromCache()
    {
        // Regression: build-only was carrying forward the previous snapshot's guide.xml verbatim.
        // When that guide was an empty stub, the new snapshot also got an empty guide even though
        // a valid cached EPG file and mappings existed.
        await using var fixture = await CreateFixtureAsync();

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var epgCacheDir = Path.Combine(tempDir, "epg-cache");
        var oldSnapDir = Path.Combine(tempDir, "old-snap");

        const string epgSourceId = "epgsrc-1";
        const string xmltvChannelId = "CBS.us";
        const string providerChannelId = "ch-cbs";
        const string fetchRunId = "run-1";

        var emptyGuide = "<?xml version=\"1.0\" encoding=\"utf-8\"?><tv generator-info-name=\"M3Undle\"></tv>";
        var cachedXmltv =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><tv>" +
            $"<channel id=\"{xmltvChannelId}\"><display-name>CBS</display-name></channel>" +
            $"<programme start=\"20260501060000 +0000\" stop=\"20260501070000 +0000\" channel=\"{xmltvChannelId}\">" +
            "<title>Morning News</title></programme></tv>";

        try
        {
            Directory.CreateDirectory(epgCacheDir);
            Directory.CreateDirectory(oldSnapDir);

            // Write cached EPG (simulates a previously fetched + stored upstream guide)
            await File.WriteAllTextAsync(Path.Combine(epgCacheDir, $"{epgSourceId}.xml"), cachedXmltv);

            // Write the empty guide that the previous snapshot points at
            var oldGuidePath = Path.Combine(oldSnapDir, "guide.xml");
            await File.WriteAllTextAsync(oldGuidePath, emptyGuide);

            await using (var setup = fixture.CreateDbContext())
            {
                setup.Profiles.Add(NewProfile("profile-1"));
                setup.Providers.Add(NewProvider("provider-1"));
                setup.ProfileProviders.Add(NewProfileProvider("provider-1", "profile-1"));
                setup.FetchRuns.Add(NewFetchRun(fetchRunId, "provider-1"));

                setup.ProviderGroups.Add(new ProviderGroup
                {
                    ProviderGroupId = "grp-1",
                    ProviderId = "provider-1",
                    RawName = "Live",
                    Active = true,
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow,
                });

                setup.ProviderChannels.Add(new ProviderChannel
                {
                    ProviderChannelId = providerChannelId,
                    ProviderId = "provider-1",
                    DisplayName = "CBS",
                    TvgId = xmltvChannelId,
                    StreamUrl = "http://example.com/live/user/pass/cbs.ts",
                    GroupTitle = "Live",
                    ProviderGroupId = "grp-1",
                    Active = true,
                    ContentType = "live",
                    LastFetchRunId = fetchRunId,
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow,
                });

                // ChannelMode = "all" (GroupModeAutoUpdate): all live channels auto-included without
                // needing per-channel ProfileGroupChannelFilter rows.
                setup.ProfileGroupFilters.Add(new ProfileGroupFilter
                {
                    ProfileGroupFilterId = "pgf-1",
                    ProfileId = "profile-1",
                    ProviderGroupId = "grp-1",
                    Decision = "include",
                    ChannelMode = "all",
                    TrackingPolicy = "review",
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                });

                // LastSuccessUtc = now + RefreshIntervalHours = 24 → cadence check is fresh → no HTTP fetch
                setup.EpgSources.Add(new EpgSource
                {
                    EpgSourceId = epgSourceId,
                    ProviderId = "provider-1",
                    Name = "Test EPG",
                    Kind = "xmltv_url",
                    UrlOrPath = "http://example.com/xmltv.xml",
                    Priority = 10,
                    Enabled = true,
                    LastSuccessUtc = DateTime.UtcNow,
                    RefreshIntervalHours = 24,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                });

                setup.EpgChannelMappings.Add(new EpgChannelMapping
                {
                    EpgChannelMappingId = "map-1",
                    ProfileId = "profile-1",
                    ProviderChannelId = providerChannelId,
                    EpgSourceId = epgSourceId,
                    XmltvChannelId = xmltvChannelId,
                    MappingMode = "manual",
                    Confidence = 1.0f,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                });

                // Previous active snapshot whose guide is an empty stub — reproduces the bug state
                setup.Snapshots.Add(new Snapshot
                {
                    SnapshotId = "snap-old",
                    ProfileId = "profile-1",
                    Status = "active",
                    CreatedUtc = DateTime.UtcNow.AddHours(-1),
                    XmltvPath = oldGuidePath,
                    // .idx file won't exist → SnapshotChangeClassifier returns null → always creates new snapshot
                    ChannelIndexPath = Path.Combine(oldSnapDir, "channel_index.ndjson"),
                    PlaylistPath = oldGuidePath,
                    StatusJsonPath = oldGuidePath,
                });

                await setup.SaveChangesAsync();
            }

            await using var db = fixture.CreateDbContext();
            var builder = CreateBuilder(db, HttpStatusCode.OK, emptyGuide, tempDir);

            await builder.BuildOnlyAsync(
                new Dictionary<string, IReadOnlyList<ParsedProviderChannel>>
                {
                    ["provider-1"] =
                    [
                        new ParsedProviderChannel
                        {
                            DisplayName = "CBS",
                            StreamUrl = "http://example.com/live/user/pass/cbs.ts",
                            GroupTitle = "Live",
                            TvgId = xmltvChannelId,
                        }
                    ]
                },
                CancellationToken.None);

            await using var verify = fixture.CreateDbContext();
            var active = await verify.Snapshots
                .Where(x => x.ProfileId == "profile-1" && x.Status == "active")
                .OrderByDescending(x => x.CreatedUtc)
                .FirstAsync();

            var guide = await File.ReadAllTextAsync(active.XmltvPath);
            Assert.IsTrue(
                guide.Contains("<programme"),
                "build-only must recompile EPG from cached sources when the previous active guide was an empty stub.");
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

    private const string SampleUpdatedM3u =
        "#EXTM3U\n" +
        "#EXTINF:-1 tvg-id=\"cnn.us\" tvg-name=\"CNN\" group-title=\"News\",CNN US HD\n" +
        "http://example.com/stream/cnn\n";

    private const string SampleNewGroupAndChannelM3u =
        "#EXTM3U\n" +
        "#EXTINF:-1 tvg-id=\"cnn.us\" tvg-name=\"CNN\" group-title=\"News\",CNN US\n" +
        "http://example.com/stream/cnn\n" +
        "#EXTINF:-1 tvg-id=\"cnn.intl\" tvg-name=\"CNN International\" group-title=\"News\",CNN International\n" +
        "http://example.com/stream/cnn-intl\n" +
        "#EXTINF:-1 tvg-id=\"espn.us\" tvg-name=\"ESPN\" group-title=\"Sports\",ESPN\n" +
        "http://example.com/stream/espn\n";

    private const string SampleMixedM3u =
        "#EXTM3U\n" +
        "#EXTINF:-1 group-title=\"News\",Live News\n" +
        "http://example.com/live/user/pass/100.ts\n" +
        "#EXTINF:-1 group-title=\"Movies\",Movie One\n" +
        "http://example.com/movie/user/pass/200.mkv\n" +
        "#EXTINF:-1 group-title=\"Series\",Series One\n" +
        "http://example.com/series/user/pass/300.mkv\n";

    // The same category name published as live, VOD and series — the shape that previously
    // collapsed into a single "mixed" group row.
    private const string SampleSameNameAcrossContentTypesM3u =
        "#EXTM3U\n" +
        "#EXTINF:-1 group-title=\"Comedy\",Comedy Central\n" +
        "http://example.com/live/user/pass/100.ts\n" +
        "#EXTINF:-1 group-title=\"Comedy\",Funny Movie\n" +
        "http://example.com/movie/user/pass/200.mkv\n" +
        "#EXTINF:-1 group-title=\"Comedy\",Funny Show — S01E01\n" +
        "http://example.com/series/user/pass/300.mkv\n";

    private const string SampleDuplicateVodTvgIdM3u =
        "#EXTM3U\n" +
        "#EXTINF:-1 tvg-id=\"movie.same\" group-title=\"Movies\",Movie One\n" +
        "http://example.com/movie/user/pass/200.mkv\n" +
        "#EXTINF:-1 tvg-id=\"movie.same\" group-title=\"Movies\",Movie Two\n" +
        "http://example.com/movie/user/pass/201.mkv\n";

    private const string SampleDuplicateLiveRowsM3u =
        "#EXTM3U\n" +
        "#EXTINF:-1 tvg-id=\"fox.ny\" group-title=\"USA FOX\",NY - NEW YORK\n" +
        "http://example.com/live/user/pass/900.ts\n" +
        "#EXTINF:-1 tvg-id=\"fox.ny\" group-title=\"USA FOX\",NY - NEW YORK\n" +
        "http://example.com/live/user/pass/900.ts\n";

    private const string SampleSameLiveChannelDifferentGroupsM3u =
        "#EXTM3U\n" +
        "#EXTINF:-1 tvg-id=\"ny.shared\" group-title=\"USA FOX\",NY - NEW YORK\n" +
        "http://example.com/live/user/pass/901.ts\n" +
        "#EXTINF:-1 tvg-id=\"ny.shared\" group-title=\"USA ABC\",NY - NEW YORK\n" +
        "http://example.com/live/user/pass/901.ts\n";

    private static SnapshotBuilder CreateBuilder(
        ApplicationDbContext db,
        HttpStatusCode statusCode,
        string content,
        string tempDir,
        IEventService? eventService = null,
        HttpMessageHandler? handler = null)
    {
        var effectiveHandler = handler ?? new FakeHttpMessageHandler(statusCode, content);
        var factory = new FakeHttpClientFactory(effectiveHandler);
        var envSvc = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envSvc);
        var activityTracker = new RefreshActivityTracker();
        var xtreamServices = new ServiceCollection();
        var xtreamSp = xtreamServices.BuildServiceProvider();
        var xtreamClient = new XtreamLineupClient(
            factory,
            xtreamSp.GetRequiredService<IServiceScopeFactory>(),
            encryption,
            activityTracker,
            new NullSeriesExpansionQueue(),
            NullLogger<XtreamLineupClient>.Instance);
        var fetcher = new ProviderFetcher(
            factory,
            new PlaylistParser(),
            envSvc,
            encryption,
            xtreamClient,
            activityTracker,
            NullLogger<ProviderFetcher>.Instance);
        var env = new FakeWebHostEnvironment(tempDir);
        var runtimePaths = new RuntimePaths(
            DataDirectory: tempDir,
            DatabasePath: Path.Combine(tempDir, "m3undle.db"),
            DatabaseConnectionString: $"Data Source={Path.Combine(tempDir, "m3undle.db")}",
            LogDirectory: tempDir,
            SnapshotDirectory: tempDir);
        var xmltvParser = new M3Undle.Core.Epg.XmltvParser();
        var epgSourceFetcher = new M3Undle.Web.Application.Epg.EpgSourceFetcher(
            factory,
            fetcher,
            envSvc,
            NullLogger<M3Undle.Web.Application.Epg.EpgSourceFetcher>.Instance);
        var epgChannelMapper = new M3Undle.Web.Application.Epg.EpgChannelMapper(
            db,
            NullLogger<M3Undle.Web.Application.Epg.EpgChannelMapper>.Instance);
        var epgCompiler = new M3Undle.Web.Application.Epg.EpgCompiler(
            NullLogger<M3Undle.Web.Application.Epg.EpgCompiler>.Instance);
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(db.Database.GetDbConnection()));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var customGroupService = new CustomGroupPageService(scopeFactory, new AppEventBus());
        var refreshScheduleService = new M3Undle.Web.Tests.Stubs.NullRefreshScheduleService();
        return new SnapshotBuilder(
            db, fetcher, epgSourceFetcher, epgChannelMapper, epgCompiler, xmltvParser,
            runtimePaths, env, Options.Create(new SnapshotOptions()), customGroupService,
            refreshScheduleService, eventService ?? new NullEventService(), TimeProvider.System, NullLogger<SnapshotBuilder>.Instance);
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

    private static Provider NewProvider(string id) => new()
    {
        ProviderId = id,
        Name = id,
        Enabled = true,
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

    private sealed class ThrowingEventService : IEventService
    {
        public Task PublishAsync(SystemEventSeverity severity, string eventType, string title, string? detail = null, string? providerId = null, string? integrationId = null)
            => throw new InvalidOperationException("event store unavailable");

        public Task<IReadOnlyList<SystemEvent>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SystemEvent>>([]);

        public Task<int> GetCountAsync(CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<SystemEventSummary> GetSummaryAsync(CancellationToken ct = default)
            => Task.FromResult(new SystemEventSummary(0, null));

        public Task DismissAsync(string eventId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DismissAllAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task CleanupOldEventsAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> HasEventAsync(string eventType, string? providerId = null, string? integrationId = null, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<int> GetRetentionDaysAsync(CancellationToken ct = default)
            => Task.FromResult(SystemEventSettings.DefaultRetentionDays);

        public Task SetRetentionDaysAsync(int days, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class RecoveryTrackingEventService : IEventService
    {
        private bool _hasBackOnline;

        public int ProviderBackOnlinePublishCount { get; private set; }

        public Task PublishAsync(SystemEventSeverity severity, string eventType, string title, string? detail = null, string? providerId = null, string? integrationId = null)
        {
            if (eventType == SystemEventTypes.ProviderBackOnline)
            {
                _hasBackOnline = true;
                ProviderBackOnlinePublishCount++;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SystemEvent>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SystemEvent>>([]);

        public Task<int> GetCountAsync(CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<SystemEventSummary> GetSummaryAsync(CancellationToken ct = default)
            => Task.FromResult(new SystemEventSummary(0, null));

        public Task DismissAsync(string eventId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DismissAllAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task CleanupOldEventsAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> HasEventAsync(string eventType, string? providerId = null, string? integrationId = null, CancellationToken ct = default)
            => Task.FromResult(eventType switch
            {
                SystemEventTypes.ProviderFetchFailed => true,
                SystemEventTypes.ProviderBackOnline => _hasBackOnline,
                _ => false,
            });

        public Task<int> GetRetentionDaysAsync(CancellationToken ct = default)
            => Task.FromResult(SystemEventSettings.DefaultRetentionDays);

        public Task SetRetentionDaysAsync(int days, CancellationToken ct = default)
            => Task.CompletedTask;
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

    private sealed class TrackingHttpMessageHandler : HttpMessageHandler
    {
        public List<string> PlaylistRequestHosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request.RequestUri?.AbsolutePath.EndsWith("/playlist.m3u", StringComparison.OrdinalIgnoreCase) == true)
                PlaylistRequestHosts.Add(request.RequestUri.Host);

            var content = request.RequestUri?.AbsolutePath.EndsWith("/xmltv.xml", StringComparison.OrdinalIgnoreCase) == true
                ? "<?xml version=\"1.0\" encoding=\"utf-8\"?><tv></tv>"
                : request.RequestUri?.AbsolutePath.EndsWith("/playlist.m3u", StringComparison.OrdinalIgnoreCase) == true
                    ? SampleM3u
                    : "{}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
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
