using System.Security.Cryptography;
using System.Text;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using static M3Undle.Web.Tests.Snapshots.SnapshotHandlingTests;

namespace M3Undle.Web.Tests.Snapshots;

/// <summary>
/// Regression coverage for the "provider changed its URL and every channel mapping vanished" incident.
/// A refresh must never destroy user intent: mapped channels and groups survive host/credential changes,
/// outages and renames; only untouched, long-inactive data is cleaned up. Hosts here are synthetic.
/// </summary>
[TestClass]
public sealed class ChannelMappingProtectionTests
{
    private const string ProviderId = "provider-1";
    private const string ProfileId = "profile-1";

    // -------------------------------------------------------------------------
    // Identity: URL changes and renames keep the row (and every mapping on it)
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Sync_WhenProviderHostAndCredentialsChange_KeepsChannelRowsAndMappings()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();

        await RunRefreshAsync(fixture, temp, Feed("old-host.test", "olduser/oldpass", (101, "Alpha", "News"), (102, "Beta", "News")));
        var idsBefore = await ChannelIdsByStreamAsync(fixture);
        await MapChannelAsync(fixture, idsBefore["101"], "News", channelNumber: 7);

        await RunRefreshAsync(fixture, temp, Feed("new-host.test:8080", "newuser/newpass", (101, "Alpha", "News"), (102, "Beta", "News")));

        await using var db = fixture.CreateDbContext();
        var channels = await db.ProviderChannels.ToListAsync();
        Assert.HasCount(2, channels);
        Assert.IsTrue(channels.All(x => x.Active), "no channel should be deactivated by a host change");
        Assert.IsTrue(channels.All(x => x.StreamUrl.StartsWith("http://new-host.test:8080/", StringComparison.Ordinal)), "stream URLs should follow the new host");

        var idsAfter = await ChannelIdsByStreamAsync(fixture);
        Assert.AreEqual(idsBefore["101"], idsAfter["101"]);
        Assert.AreEqual(idsBefore["102"], idsAfter["102"]);

        var mapping = await db.ProfileGroupChannelFilters.SingleAsync();
        Assert.AreEqual(idsBefore["101"], mapping.ProviderChannelId);
        Assert.AreEqual(7, mapping.ChannelNumber);
        Assert.AreEqual(1, await db.ProfileCustomGroupChannels.CountAsync(x => x.ProviderChannelId == idsBefore["101"]));
    }

    [TestMethod]
    public async Task Sync_WhenOnlyCredentialsChange_KeepsChannelRows()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();

        await RunRefreshAsync(fixture, temp, Feed("host.test", "user/oldpass", (101, "Alpha", "News")));
        var before = await ChannelIdsByStreamAsync(fixture);

        await RunRefreshAsync(fixture, temp, Feed("host.test", "user/newpass", (101, "Alpha", "News")));

        var after = await ChannelIdsByStreamAsync(fixture);
        Assert.AreEqual(before["101"], after["101"]);
        await using var db = fixture.CreateDbContext();
        Assert.AreEqual(1, await db.ProviderChannels.CountAsync());
    }

    [TestMethod]
    public async Task Sync_WhenChannelIsRenamed_KeepsRowAndUpdatesName()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();

        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", (101, "Match Slot 1", "Events")));
        var before = await ChannelIdsByStreamAsync(fixture);

        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", (101, "Team A vs Team B", "Events")));

        await using var db = fixture.CreateDbContext();
        var channel = await db.ProviderChannels.SingleAsync();
        Assert.AreEqual(before["101"], channel.ProviderChannelId);
        Assert.AreEqual("Team A vs Team B", channel.DisplayName);
        Assert.IsTrue(channel.Active);
    }

    [TestMethod]
    public async Task Sync_WhenRowsCarryLegacyKeys_AdoptsThemInPlaceAndKeepsTheirKeys()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();
        var feed = Feed("host.test", "u/p", (101, "Alpha", "News"), (102, "Beta", "News"));

        await RunRefreshAsync(fixture, temp, feed);
        var before = await ChannelIdsByStreamAsync(fixture);
        await RewriteKeysToLegacyAsync(fixture);
        var legacyKeys = await ChannelKeysAsync(fixture);

        await RunRefreshAsync(fixture, temp, feed);

        await using var verify = fixture.CreateDbContext();
        var channels = await verify.ProviderChannels.ToListAsync();
        Assert.HasCount(2, channels, "legacy rows must be adopted, not duplicated");
        Assert.IsTrue(channels.All(x => x.Active));
        Assert.AreEqual(before["101"], (await ChannelIdsByStreamAsync(fixture))["101"]);
        CollectionAssert.AreEqual(legacyKeys, await ChannelKeysAsync(fixture),
            "the key feeds the published StreamKey, so an exact legacy match must not rewrite it");

        // Still matched on the following refresh.
        await RunRefreshAsync(fixture, temp, feed);
        await using var again = fixture.CreateDbContext();
        Assert.AreEqual(2, await again.ProviderChannels.CountAsync());
    }

    [TestMethod]
    public async Task Upgrade_DoesNotChangePublishedStreamKeys()
    {
        // Downstream clients track the published StreamKey. Deploying the identity change must not
        // make every channel look new to Jellyfin / NextPVR / HDHR.
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();
        var feed = Feed("host.test", "u/p", (101, "Alpha", "News"), (102, "Beta", "News"));

        await RunRefreshAsync(fixture, temp, feed);
        await using (var modeDb = fixture.CreateDbContext())
        {
            await modeDb.ProfileGroupFilters.ExecuteUpdateAsync(s => s.SetProperty(x => x.ChannelMode, LineupReviewSemantics.GroupModeAutoUpdate));
        }

        // The state a pre-upgrade deployment is in: legacy keys, snapshot published from them.
        await RewriteKeysToLegacyAsync(fixture);
        await using (var buildDb = fixture.CreateDbContext())
        {
            var built = await CreateBuilder(buildDb, HttpStatusCode.OK, feed, temp.Path).BuildOnlyAsync(CancellationToken.None);
            Assert.IsTrue(built.Succeeded);
        }
        var before = await PublishedStreamKeysAsync(fixture);
        Assert.HasCount(2, before);

        // First refresh on the new code, with a changed lineup so a new snapshot is published.
        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", (101, "Alpha", "News"), (102, "Beta", "News"), (103, "Gamma", "News")));

        var after = await PublishedStreamKeysAsync(fixture);
        Assert.HasCount(3, after);
        Assert.AreEqual(before["Alpha"], after["Alpha"]);
        Assert.AreEqual(before["Beta"], after["Beta"]);
    }

    [TestMethod]
    public async Task Sync_WhenStreamIdChangesButGroupAndNameMatchUniquely_AdoptsRow()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();
        await SeedChannelAsync(fixture, "existing-solo", "Solo", "News", "http://a.test/live/u/p/1.ts", active: true, key: "k-solo");

        await RunRefreshAsync(fixture, temp, Feed("c.test", "u/p", (3, "Solo", "News")));

        await using var db = fixture.CreateDbContext();
        var channel = await db.ProviderChannels.SingleAsync();
        Assert.AreEqual("existing-solo", channel.ProviderChannelId);
        Assert.AreEqual("http://c.test/live/u/p/3.ts", channel.StreamUrl);
        Assert.IsTrue(channel.Active);
    }

    [TestMethod]
    public async Task Sync_WhenFallbackMatchIsAmbiguous_DoesNotAdoptAndKeepsOldRows()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();
        await SeedChannelAsync(fixture, "dup-a", "Dup", "News", "http://a.test/live/u/p/1.ts", active: true, key: "k-a");
        await SeedChannelAsync(fixture, "dup-b", "Dup", "News", "http://b.test/live/u/p/2.ts", active: true, key: "k-b");

        await RunRefreshAsync(fixture, temp, Feed("c.test", "u/p", (3, "Dup", "News")));

        await using var db = fixture.CreateDbContext();
        var channels = await db.ProviderChannels.ToListAsync();
        Assert.HasCount(3, channels, "an ambiguous match must create a new row rather than guess");
        Assert.IsFalse(channels.Single(x => x.ProviderChannelId == "dup-a").Active);
        Assert.IsFalse(channels.Single(x => x.ProviderChannelId == "dup-b").Active);
        Assert.AreEqual(1, channels.Count(x => x.Active));
    }

    // -------------------------------------------------------------------------
    // Mass-change visibility
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Sync_WhenMostChannelsDisappear_PublishesMassChangeEvent()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();
        var events = new CapturingEventService();

        // Same size, entirely different channels: the count gate lets it through, but nothing matches.
        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1, 60, "Old")), events);
        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1001, 60, "New")), events);

        Assert.AreEqual(1, events.Count(SystemEventTypes.ProviderLineupMassChange));
    }

    [TestMethod]
    public async Task Sync_WhenLineupIsUnchanged_DoesNotPublishMassChangeEvent()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();
        var events = new CapturingEventService();
        var feed = Feed("host.test", "u/p", Range(1, 60, "Chan"));

        await RunRefreshAsync(fixture, temp, feed, events);
        await RunRefreshAsync(fixture, temp, feed, events);

        Assert.AreEqual(0, events.Count(SystemEventTypes.ProviderLineupMassChange));
    }

    [TestMethod]
    public async Task Sync_WhenSmallProviderLosesChannels_DoesNotPublishMassChangeEvent()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();
        var events = new CapturingEventService();

        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1, 10, "Old")), events);
        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1001, 10, "New")), events);

        Assert.AreEqual(0, events.Count(SystemEventTypes.ProviderLineupMassChange));
    }

    // -------------------------------------------------------------------------
    // Retention purge: never destroys mapped channels, ignores failed runs
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Purge_AfterManyFailedRuns_DoesNotDeleteRecentlySeenChannels()
    {
        // The incident shape: one old good run, a long streak of failures, then a good run whose
        // rekeyed lineup deactivates everything from the old run.
        await using var fixture = await CreateSeededFixtureAsync();
        var now = DateTime.UtcNow;
        await SeedRunsAsync(fixture, now);
        await SeedChannelAsync(fixture, "c1", "One", "News", "http://a.test/live/u/p/1.ts", active: false, lastSeenUtc: now.AddDays(-10), runId: "run-old");
        await SeedChannelAsync(fixture, "c2", "Two", "News", "http://a.test/live/u/p/2.ts", active: false, lastSeenUtc: now.AddDays(-10), runId: "run-old");

        await using var db = fixture.CreateDbContext();
        var result = await SnapshotRefreshService.PurgeProviderGenerationsAsync(db, ProviderId, 30, now, CancellationToken.None);

        Assert.AreEqual(0, result.Channels);
        Assert.AreEqual(2, await db.ProviderChannels.CountAsync());
    }

    [TestMethod]
    public async Task Purge_PastGracePeriod_KeepsMappedChannelsAndRemovesUntouchedOnes()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        var now = DateTime.UtcNow;
        var stale = now.AddDays(-40);
        await SeedRunsAsync(fixture, now);

        await SeedChannelAsync(fixture, "mapped-included", "A", "News", "http://a.test/live/u/p/1.ts", active: false, lastSeenUtc: stale, runId: "run-old");
        await SeedChannelAsync(fixture, "mapped-override", "B", "News", "http://a.test/live/u/p/2.ts", active: false, lastSeenUtc: stale, runId: "run-old");
        await SeedChannelAsync(fixture, "in-custom-group", "C", "News", "http://a.test/live/u/p/3.ts", active: false, lastSeenUtc: stale, runId: "run-old");
        await SeedChannelAsync(fixture, "manual-epg", "D", "News", "http://a.test/live/u/p/4.ts", active: false, lastSeenUtc: stale, runId: "run-old");
        await SeedChannelAsync(fixture, "plain-with-rows", "E", "News", "http://a.test/live/u/p/5.ts", active: false, lastSeenUtc: stale, runId: "run-old");
        await SeedChannelAsync(fixture, "plain-bare", "F", "News", "http://a.test/live/u/p/6.ts", active: false, lastSeenUtc: stale, runId: "run-old");
        await SeedChannelAsync(fixture, "active-stale-date", "G", "News", "http://a.test/live/u/p/7.ts", active: true, lastSeenUtc: stale, runId: "run-new");

        string filterId;
        await using (var setup = fixture.CreateDbContext())
        {
            filterId = await SeedGroupFilterAsync(setup, "News", decision: "include", isNew: false);
            setup.ProfileGroupChannelFilters.AddRange(
                ChannelFilter(filterId, "mapped-included", "included"),
                ChannelFilter(filterId, "mapped-override", "pending", channelNumber: 12),
                ChannelFilter(filterId, "plain-with-rows", "pending"),
                ChannelFilter(filterId, "plain-bare", "excluded"));

            setup.ProfileCustomGroups.Add(new ProfileCustomGroup { CustomGroupId = "cg-1", ProfileId = ProfileId, Name = "Mine", CreatedUtc = now, UpdatedUtc = now });
            setup.ProfileCustomGroupChannels.Add(new ProfileCustomGroupChannel
            {
                CustomGroupChannelId = "cgc-1", CustomGroupId = "cg-1", ProviderChannelId = "in-custom-group", State = "included", CreatedUtc = now, UpdatedUtc = now,
            });

            setup.EpgSources.Add(new EpgSource { EpgSourceId = "epg-1", Name = "epg", UrlOrPath = "http://epg.test/x.xml", CreatedUtc = now, UpdatedUtc = now });
            setup.EpgChannelMappings.AddRange(
                EpgMapping("epg-1", "manual-epg", "manual", now),
                EpgMapping("epg-1", "plain-with-rows", "auto_id", now));
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var result = await SnapshotRefreshService.PurgeProviderGenerationsAsync(db, ProviderId, 30, now, CancellationToken.None);

        Assert.AreEqual(2, result.Channels, "only the two untouched channels may be purged");
        Assert.AreEqual(4, result.ProtectedChannels);

        var surviving = (await db.ProviderChannels.Select(x => x.ProviderChannelId).ToListAsync()).Order().ToList();
        CollectionAssert.AreEqual(
            new[] { "active-stale-date", "in-custom-group", "manual-epg", "mapped-included", "mapped-override" },
            surviving);

        // Mappings on protected channels are untouched; rows on purged channels are cleaned up with them.
        Assert.AreEqual(2, await db.ProfileGroupChannelFilters.CountAsync());
        Assert.AreEqual(1, await db.ProfileCustomGroupChannels.CountAsync());
        var remainingEpg = await db.EpgChannelMappings.ToListAsync();
        Assert.HasCount(1, remainingEpg);
        Assert.AreEqual("manual-epg", remainingEpg[0].ProviderChannelId);
    }

    [TestMethod]
    public async Task Purge_TrimsFailedRunsButKeepsRunsStillReferencedByChannels()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        var now = DateTime.UtcNow;
        await SeedRunsAsync(fixture, now);
        // A protected, mapped channel still points at the old good run.
        await SeedChannelAsync(fixture, "mapped", "A", "News", "http://a.test/live/u/p/1.ts", active: false, lastSeenUtc: now.AddDays(-40), runId: "run-old");
        await using (var setup = fixture.CreateDbContext())
        {
            var filterId = await SeedGroupFilterAsync(setup, "News", decision: "include", isNew: false);
            setup.ProfileGroupChannelFilters.Add(ChannelFilter(filterId, "mapped", "included"));
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var result = await SnapshotRefreshService.PurgeProviderGenerationsAsync(db, ProviderId, 30, now, CancellationToken.None);

        var runIds = (await db.FetchRuns.Select(x => x.FetchRunId).ToListAsync()).Order().ToList();
        Assert.IsTrue(runIds.Contains("run-old"), "a run referenced by a surviving channel must not be deleted");
        Assert.IsTrue(runIds.Contains("run-new"), "the newest run is always kept");
        Assert.IsFalse(runIds.Contains("run-fail-1"), "old failed runs are trimmed");
        Assert.IsGreaterThan(0, result.Runs);
    }

    // -------------------------------------------------------------------------
    // Snapshot retention keeps a known-good older snapshot
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task SnapshotRetention_KeepsNewestSnapshotOlderThanSafetyAge()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();
        var options = new SnapshotOptions { RetentionCount = 1, SafetySnapshotAgeHours = 24 };

        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1, 5, "Chan")), snapshotOptions: options);

        // A group only publishes channels once the user selects them; "all" mode publishes the whole group,
        // so the next, changed feed produces a new snapshot (a no-op refresh skips retention entirely).
        await using (var modeDb = fixture.CreateDbContext())
        {
            await modeDb.ProfileGroupFilters.ExecuteUpdateAsync(s => s.SetProperty(x => x.ChannelMode, LineupReviewSemantics.GroupModeAutoUpdate));
        }

        var now = DateTime.UtcNow;
        await using (var setup = fixture.CreateDbContext())
        {
            setup.Snapshots.AddRange(
                ArchivedSnapshot("snap-48h", now.AddHours(-48)),
                ArchivedSnapshot("snap-30h", now.AddHours(-30)),
                ArchivedSnapshot("snap-1h", now.AddHours(-1)));
            await setup.SaveChangesAsync();
        }

        // A changed lineup publishes a new snapshot, which triggers retention.
        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1, 6, "Chan")), snapshotOptions: options);

        await using var db = fixture.CreateDbContext();
        var snapshotIds = await db.Snapshots.Select(x => x.SnapshotId).ToListAsync();
        Assert.HasCount(2, snapshotIds);
        Assert.IsTrue(snapshotIds.Contains("snap-30h"), "the newest snapshot older than 24h is the known-good safety copy");
        Assert.IsFalse(snapshotIds.Contains("snap-48h"));
        Assert.IsFalse(snapshotIds.Contains("snap-1h"));
    }

    // -------------------------------------------------------------------------
    // Stale group cleanup: unused groups go, mapped groups stay
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task StaleGroupCleanup_RemovesUnusedGroupsAndKeepsAnythingTheUserMapped()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();
        var feed = Feed("host.test", "u/p", (1, "Alpha", "News"));
        await RunRefreshAsync(fixture, temp, feed);

        var now = DateTime.UtcNow;
        await using (var setup = fixture.CreateDbContext())
        {
            foreach (var name in new[] { "reviewed-include", "excluded-bare", "custom-linked", "has-channel", "unused", "unreviewed-include", "notify", "named-output" })
                setup.ProviderGroups.Add(StaleGroup(name, now));
            await setup.SaveChangesAsync();

            await SeedGroupFilterAsync(setup, "reviewed-include", decision: "include", isNew: false);
            await SeedGroupFilterAsync(setup, "excluded-bare", decision: "exclude", isNew: false);
            await SeedGroupFilterAsync(setup, "unreviewed-include", decision: "include", isNew: true);
            await SeedGroupFilterAsync(setup, "notify", decision: "exclude", isNew: false, trackNew: true);
            await SeedGroupFilterAsync(setup, "named-output", decision: "exclude", isNew: false, outputName: "Renamed");

            setup.ProfileCustomGroups.Add(new ProfileCustomGroup { CustomGroupId = "cg-1", ProfileId = ProfileId, Name = "Mine", CreatedUtc = now, UpdatedUtc = now });
            setup.ProfileCustomGroupProviderLinks.Add(new ProfileCustomGroupProviderLink
            {
                LinkId = "link-1", CustomGroupId = "cg-1", ProviderGroupId = "stale-custom-linked", CreatedUtc = now,
            });
            await setup.SaveChangesAsync();
        }
        await SeedChannelAsync(fixture, "orphan-candidate", "X", "has-channel", "http://a.test/live/u/p/9.ts", active: false, groupId: "stale-has-channel");

        await RunRefreshAsync(fixture, temp, feed);

        await using var db = fixture.CreateDbContext();
        var names = (await db.ProviderGroups.Select(x => x.RawName).ToListAsync()).Order(StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(
            new[] { "News", "custom-linked", "has-channel", "named-output", "notify", "reviewed-include" },
            names);
        Assert.AreEqual(1, await db.ProfileCustomGroupProviderLinks.CountAsync(), "removing a group would cascade-delete its custom-group link");
    }

    // -------------------------------------------------------------------------
    // Suspect fetches: a fetch that looks wrong writes nothing
    // -------------------------------------------------------------------------

    [TestMethod]
    [DataRow(30, 20, false, DisplayName = "30 -> 20 is a normal change")]
    [DataRow(30, 10, true, DisplayName = "30 -> 10 lost two thirds")]
    [DataRow(10000, 30, true, DisplayName = "10,000 -> 30 is not a lineup change")]
    [DataRow(10000, 5000, false, DisplayName = "exactly half is still accepted")]
    [DataRow(10000, 4999, true, DisplayName = "just under half is held")]
    [DataRow(5, 1, false, DisplayName = "tiny lineups only trip on empty")]
    [DataRow(5, 0, true, DisplayName = "empty fetch against a lineup is held")]
    [DataRow(0, 0, false, DisplayName = "nothing to protect on the first sync")]
    [DataRow(100, 5000, false, DisplayName = "growth only adds rows")]
    public void IsSuspectLineupDrop_ComparesAgainstThePreviousLineup(int previous, int incoming, bool expected)
        => Assert.AreEqual(expected, SnapshotBuilder.IsSuspectLineupDrop(previous, incoming, 0.5));

    [TestMethod]
    public async Task Refresh_WhenFetchLosesMostOfTheLineup_WritesNothingAndKeepsLastKnownGood()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();
        var events = new CapturingEventService();

        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1, 60, "Chan")), events);
        var before = await ChannelIdsByStreamAsync(fixture);
        await MapChannelAsync(fixture, before["1"], "News", channelNumber: 5);

        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1, 10, "Chan")), events);

        await using var db = fixture.CreateDbContext();
        Assert.AreEqual(60, await db.ProviderChannels.CountAsync(x => x.Active), "no channel may be deactivated by a held fetch");
        Assert.AreEqual(1, await db.ProviderGroups.CountAsync(x => x.Active && x.ChannelCount == 60), "group counts are untouched too");
        Assert.AreEqual(1, await db.ProfileGroupChannelFilters.CountAsync());

        var run = await db.FetchRuns.OrderByDescending(x => x.StartedUtc).FirstAsync();
        Assert.AreEqual("suspect", run.Status);
        StringAssert.Contains(run.ErrorSummary, "10 live channel");
        StringAssert.Contains(run.ErrorSummary, "60 are currently active");
        Assert.AreEqual(1, events.Count(SystemEventTypes.ProviderFetchSuspect));
    }

    [TestMethod]
    public async Task Refresh_WhenFetchIsEmpty_KeepsLastKnownGood()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();

        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1, 60, "Chan")));
        await RunRefreshAsync(fixture, temp, "#EXTM3U\n");

        await using var db = fixture.CreateDbContext();
        Assert.AreEqual(60, await db.ProviderChannels.CountAsync(x => x.Active));
        var run = await db.FetchRuns.OrderByDescending(x => x.StartedUtc).FirstAsync();
        Assert.AreNotEqual("ok", run.Status);
    }

    [TestMethod]
    public async Task Refresh_WhenHeldFetchRepeatsUnchanged_IsAcceptedOnTheThirdRun()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();
        var events = new CapturingEventService();
        var reduced = Feed("host.test", "u/p", Range(1, 10, "Chan"));

        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1, 60, "Chan")), events);

        await RunRefreshAsync(fixture, temp, reduced, events);
        await RunRefreshAsync(fixture, temp, reduced, events);
        await using (var held = fixture.CreateDbContext())
        {
            Assert.AreEqual(60, await held.ProviderChannels.CountAsync(x => x.Active), "still held after two matching fetches");
            Assert.AreEqual(2, await held.FetchRuns.CountAsync(x => x.Status == "suspect"));
        }

        await RunRefreshAsync(fixture, temp, reduced, events);

        await using var db = fixture.CreateDbContext();
        Assert.AreEqual(10, await db.ProviderChannels.CountAsync(x => x.Active));
        Assert.AreEqual(60, await db.ProviderChannels.CountAsync(), "the 50 missing channels are deactivated, never deleted here");
        Assert.AreEqual(2, events.Count(SystemEventTypes.ProviderFetchSuspect), "one warning for the streak and one when it was accepted");
    }

    [TestMethod]
    public async Task Refresh_WhenHeldFetchesDiffer_KeepsHolding()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();

        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1, 60, "Chan")));
        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1, 5, "Chan")));
        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1, 20, "Chan")));
        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1, 12, "Chan")));

        await using var db = fixture.CreateDbContext();
        Assert.AreEqual(60, await db.ProviderChannels.CountAsync(x => x.Active), "an unstable provider is never trusted");
        Assert.AreEqual(3, await db.FetchRuns.CountAsync(x => x.Status == "suspect"));
    }

    [TestMethod]
    public async Task Refresh_WhenHealthyFetchFollowsAHeldOne_ResetsTheStreak()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();
        var reduced = Feed("host.test", "u/p", Range(1, 10, "Chan"));

        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1, 60, "Chan")));
        await RunRefreshAsync(fixture, temp, reduced);
        await RunRefreshAsync(fixture, temp, reduced);
        await RunRefreshAsync(fixture, temp, Feed("host.test", "u/p", Range(1, 60, "Chan")));   // provider recovered
        await RunRefreshAsync(fixture, temp, reduced);                                           // new streak starts at one

        await using var db = fixture.CreateDbContext();
        Assert.AreEqual(60, await db.ProviderChannels.CountAsync(x => x.Active));
    }

    [TestMethod]
    public async Task Refresh_WhenUserExcludedMostOfTheLineup_IsNotMistakenForAnOutage()
    {
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();
        var feed = Feed("host.test", "u/p",
            Enumerable.Range(1, 20).Select(i => (i, $"A {i}", "Keep"))
                .Concat(Enumerable.Range(101, 80).Select(i => (i, $"B {i}", "Drop"))).ToArray());

        await RunRefreshAsync(fixture, temp, feed);

        // The user excludes the big group; its 80 channels are expected to go.
        await using (var setup = fixture.CreateDbContext())
        {
            var filter = await setup.ProfileGroupFilters.Include(x => x.ProviderGroup).SingleAsync(x => x.ProviderGroup.RawName == "Drop");
            filter.Decision = LineupReviewSemantics.GroupDecisionExclude;
            await setup.SaveChangesAsync();
        }

        await RunRefreshAsync(fixture, temp, feed);

        await using var db = fixture.CreateDbContext();
        Assert.AreEqual(0, await db.FetchRuns.CountAsync(x => x.Status == "suspect"));
        Assert.AreEqual(20, await db.ProviderChannels.CountAsync(x => x.Active));
    }

    [TestMethod]
    public async Task Refresh_WhenOnlyExcludedGroupsRemain_IsHeldBecauseTheLineupInUseVanished()
    {
        // Counting raw feed size would call this healthy (80 channels vs 20), but every channel the
        // user actually publishes is gone. The check must look at the lineup in use.
        await using var fixture = await CreateSeededFixtureAsync();
        using var temp = new TempDir();
        var both = Feed("host.test", "u/p",
            Enumerable.Range(1, 20).Select(i => (i, $"A {i}", "Keep"))
                .Concat(Enumerable.Range(101, 80).Select(i => (i, $"B {i}", "Drop"))).ToArray());
        var onlyExcluded = Feed("host.test", "u/p", Enumerable.Range(101, 80).Select(i => (i, $"B {i}", "Drop")).ToArray());

        await RunRefreshAsync(fixture, temp, both);
        await using (var setup = fixture.CreateDbContext())
        {
            var filter = await setup.ProfileGroupFilters.Include(x => x.ProviderGroup).SingleAsync(x => x.ProviderGroup.RawName == "Drop");
            filter.Decision = LineupReviewSemantics.GroupDecisionExclude;
            await setup.SaveChangesAsync();
        }
        await RunRefreshAsync(fixture, temp, both);

        await RunRefreshAsync(fixture, temp, onlyExcluded);

        await using var db = fixture.CreateDbContext();
        Assert.AreEqual(20, await db.ProviderChannels.CountAsync(x => x.Active && x.GroupTitle == "Keep"));
        Assert.AreEqual(1, await db.FetchRuns.CountAsync(x => x.Status == "suspect"));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task<TestFixture> CreateSeededFixtureAsync()
    {
        var fixture = await CreateFixtureAsync();
        await using var setup = fixture.CreateDbContext();
        setup.Profiles.Add(NewProfile(ProfileId));
        setup.Providers.Add(NewProvider(ProviderId));
        setup.ProfileProviders.Add(NewProfileProvider(ProviderId, ProfileId));
        await setup.SaveChangesAsync();
        return fixture;
    }

    private static async Task RunRefreshAsync(
        TestFixture fixture,
        TempDir temp,
        string feed,
        IEventService? events = null,
        SnapshotOptions? snapshotOptions = null)
    {
        await using var db = fixture.CreateDbContext();
        await CreateBuilder(db, HttpStatusCode.OK, feed, temp.Path, events, snapshotOptions: snapshotOptions)
            .RunAsync(CancellationToken.None);
    }

    private static string Feed(string host, string credentials, params (int Id, string Name, string Group)[] channels)
        => "#EXTM3U\n" + string.Concat(channels.Select(c =>
            $"#EXTINF:-1 tvg-id=\"ch{c.Id}\" group-title=\"{c.Group}\",{c.Name}\nhttp://{host}/live/{credentials}/{c.Id}.ts\n"));

    private static (int, string, string)[] Range(int startId, int count, string prefix)
        => Enumerable.Range(startId, count).Select(i => (i, $"{prefix} {i}", "News")).ToArray();

    private static string StreamId(string streamUrl)
        => System.IO.Path.GetFileNameWithoutExtension(new Uri(streamUrl).AbsolutePath);

    private static async Task<Dictionary<string, string>> ChannelIdsByStreamAsync(TestFixture fixture)
    {
        await using var db = fixture.CreateDbContext();
        return (await db.ProviderChannels.ToListAsync()).ToDictionary(x => StreamId(x.StreamUrl), x => x.ProviderChannelId);
    }

    // The pre-v2 identity: raw stream URL and display name were part of the hash.
    private static string LegacyKey(string tvgId, string streamUrl, string? group, string displayName)
    {
        var identity = $"{tvgId}\u001f{streamUrl}\u001f{group}\u001f{displayName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=')[..16];
    }

    // The pre-upgrade state: rows keyed by the old identity formula (raw URL and name in the hash).
    private static async Task RewriteKeysToLegacyAsync(TestFixture fixture)
    {
        await using var db = fixture.CreateDbContext();
        foreach (var channel in await db.ProviderChannels.ToListAsync())
            channel.ProviderChannelKey = LegacyKey($"ch{StreamId(channel.StreamUrl)}", channel.StreamUrl, channel.GroupTitle, channel.DisplayName);
        await db.SaveChangesAsync();
    }

    private static async Task<List<string?>> ChannelKeysAsync(TestFixture fixture)
    {
        await using var db = fixture.CreateDbContext();
        return (await db.ProviderChannels.Select(x => x.ProviderChannelKey).ToListAsync()).Order(StringComparer.Ordinal).ToList();
    }

    // DisplayName -> StreamKey from the active snapshot's channel index.
    private static async Task<Dictionary<string, string>> PublishedStreamKeysAsync(TestFixture fixture)
    {
        await using var db = fixture.CreateDbContext();
        var path = await db.Snapshots.Where(x => x.Status == "active").OrderByDescending(x => x.CreatedUtc).Select(x => x.ChannelIndexPath).FirstAsync();
        var keys = new Dictionary<string, string>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            string Read(string name) => doc.RootElement.EnumerateObject()
                .First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
            keys[Read("displayName")] = Read("streamKey");
        }
        return keys;
    }

    private static async Task MapChannelAsync(TestFixture fixture, string providerChannelId, string groupName, int channelNumber)
    {
        var now = DateTime.UtcNow;
        await using var db = fixture.CreateDbContext();
        var filterId = await db.ProfileGroupFilters
            .Where(x => x.ProfileId == ProfileId && x.ProviderGroup.RawName == groupName)
            .Select(x => x.ProfileGroupFilterId)
            .SingleAsync();

        db.ProfileGroupChannelFilters.Add(ChannelFilter(filterId, providerChannelId, "included", channelNumber));
        db.ProfileCustomGroups.Add(new ProfileCustomGroup { CustomGroupId = "cg-1", ProfileId = ProfileId, Name = "Mine", CreatedUtc = now, UpdatedUtc = now });
        db.ProfileCustomGroupChannels.Add(new ProfileCustomGroupChannel
        {
            CustomGroupChannelId = "cgc-1", CustomGroupId = "cg-1", ProviderChannelId = providerChannelId, State = "included", CreatedUtc = now, UpdatedUtc = now,
        });
        await db.SaveChangesAsync();
    }

    private static ProfileGroupChannelFilter ChannelFilter(string filterId, string providerChannelId, string state, int? channelNumber = null) => new()
    {
        ProfileGroupChannelFilterId = $"pgcf-{providerChannelId}",
        ProfileGroupFilterId = filterId,
        ProviderChannelId = providerChannelId,
        State = state,
        ChannelNumber = channelNumber,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static EpgChannelMapping EpgMapping(string sourceId, string providerChannelId, string mode, DateTime now) => new()
    {
        EpgChannelMappingId = $"epgm-{providerChannelId}",
        ProfileId = ProfileId,
        ProviderChannelId = providerChannelId,
        EpgSourceId = sourceId,
        XmltvChannelId = $"x-{providerChannelId}",
        MappingMode = mode,
        CreatedUtc = now,
        UpdatedUtc = now,
    };

    private static ProviderGroup StaleGroup(string name, DateTime now) => new()
    {
        ProviderGroupId = $"stale-{name}",
        ProviderId = ProviderId,
        RawName = name,
        ContentType = "live",
        Active = false,
        ChannelCount = 0,
        FirstSeenUtc = now.AddDays(-90),
        LastSeenUtc = now.AddDays(-30),
    };

    private static async Task<string> SeedGroupFilterAsync(
        ApplicationDbContext db, string groupName, string decision, bool isNew, bool trackNew = false, string? outputName = null)
    {
        var groupId = await db.ProviderGroups
            .Where(x => x.ProviderId == ProviderId && x.RawName == groupName)
            .Select(x => x.ProviderGroupId)
            .FirstOrDefaultAsync();

        if (groupId is null)
        {
            groupId = $"group-{groupName}";
            db.ProviderGroups.Add(new ProviderGroup
            {
                ProviderGroupId = groupId, ProviderId = ProviderId, RawName = groupName, ContentType = "live",
                Active = true, ChannelCount = 1, FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var filterId = $"pgf-{groupName}";
        db.ProfileGroupFilters.Add(new ProfileGroupFilter
        {
            ProfileGroupFilterId = filterId,
            ProfileId = ProfileId,
            ProviderGroupId = groupId,
            Decision = decision,
            IsNew = isNew,
            TrackNewChannels = trackNew,
            OutputName = outputName,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return filterId;
    }

    private static async Task SeedRunsAsync(TestFixture fixture, DateTime now)
    {
        await using var db = fixture.CreateDbContext();
        db.FetchRuns.Add(new FetchRun { FetchRunId = "run-old", ProviderId = ProviderId, StartedUtc = now.AddDays(-10), Status = "ok" });
        for (var i = 1; i <= 5; i++)
            db.FetchRuns.Add(new FetchRun { FetchRunId = $"run-fail-{i}", ProviderId = ProviderId, StartedUtc = now.AddDays(-9).AddHours(i), Status = "fail" });
        db.FetchRuns.Add(new FetchRun { FetchRunId = "run-new", ProviderId = ProviderId, StartedUtc = now.AddMinutes(-1), Status = "ok" });
        await db.SaveChangesAsync();
    }

    private static async Task SeedChannelAsync(
        TestFixture fixture,
        string id,
        string name,
        string group,
        string streamUrl,
        bool active,
        string? key = null,
        DateTime? lastSeenUtc = null,
        string runId = "run-seed",
        string? groupId = null)
    {
        await using var db = fixture.CreateDbContext();
        if (!await db.FetchRuns.AnyAsync(x => x.FetchRunId == runId))
        {
            db.FetchRuns.Add(new FetchRun { FetchRunId = runId, ProviderId = ProviderId, StartedUtc = DateTime.UtcNow.AddDays(-60), Status = "ok" });
            await db.SaveChangesAsync();
        }

        db.ProviderChannels.Add(new ProviderChannel
        {
            ProviderChannelId = id,
            ProviderId = ProviderId,
            ProviderChannelKey = key ?? $"key-{id}",
            DisplayName = name,
            StreamUrl = streamUrl,
            GroupTitle = group,
            ProviderGroupId = groupId,
            ContentType = "live",
            Active = active,
            FirstSeenUtc = DateTime.UtcNow.AddDays(-60),
            LastSeenUtc = lastSeenUtc ?? DateTime.UtcNow,
            LastFetchRunId = runId,
        });
        await db.SaveChangesAsync();
    }

    private static Snapshot ArchivedSnapshot(string id, DateTime createdUtc) => new()
    {
        SnapshotId = id,
        ProfileId = ProfileId,
        CreatedUtc = createdUtc,
        Status = "archived",
        PlaylistPath = "playlist.m3u",
        XmltvPath = "guide.xml",
        ChannelIndexPath = "channel_index.ndjson",
        StatusJsonPath = "status.json",
    };

    private sealed class CapturingEventService : IEventService
    {
        private readonly List<string> _published = [];

        public int Count(string eventType) => _published.Count(x => x == eventType);

        public Task PublishAsync(SystemEventSeverity severity, string eventType, string title, string? detail = null, string? providerId = null, string? integrationId = null, string? epgSourceId = null)
        {
            _published.Add(eventType);
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

        public Task<bool> HasEventAsync(string eventType, string? providerId = null, string? integrationId = null, CancellationToken ct = default, string? epgSourceId = null)
            => Task.FromResult(false);

        public Task<int> GetRetentionDaysAsync(CancellationToken ct = default)
            => Task.FromResult(SystemEventSettings.DefaultRetentionDays);

        public Task SetRetentionDaysAsync(int days, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
