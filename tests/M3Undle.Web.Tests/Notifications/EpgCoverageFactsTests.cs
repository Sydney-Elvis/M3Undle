using M3Undle.Core.Epg;
using M3Undle.Web.Application;
using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

[TestClass]
public sealed class EpgCoverageFactsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Intervals_AreMergedBoundedAndRoundTrip()
    {
        var intervals = new[]
        {
            new EpgInterval(Now, Now.AddHours(1)),
            new EpgInterval(Now.AddHours(1), Now.AddHours(2)),
            new EpgInterval(Now.AddHours(3), Now.AddHours(4)),
            new EpgInterval(Now.AddDays(-3), Now.AddDays(-2)),
            new EpgInterval(Now.AddDays(30), Now.AddDays(31)),
            new EpgInterval(Now.AddHours(5), Now.AddHours(5)),
        };

        var (encoded, count) = EpgCoverageFacts.EncodeIntervals(intervals, Now);
        var decoded = EpgCoverageFacts.DecodeIntervals(encoded);

        Assert.AreEqual(2, count, "Adjacent programmes merge; stale, far-future and zero-length intervals are dropped.");
        Assert.AreEqual(new EpgInterval(Now, Now.AddHours(2)), decoded[0]);
        Assert.AreEqual(new EpgInterval(Now.AddHours(3), Now.AddHours(4)), decoded[1]);
        Assert.IsEmpty(EpgCoverageFacts.DecodeIntervals("garbage;1-;-2;x-y"));
    }

    [TestMethod]
    public void Intervals_AreCappedPerChannel()
    {
        var many = Enumerable.Range(0, 1000).Select(i => new EpgInterval(Now.AddMinutes(i * 10), Now.AddMinutes(i * 10 + 5)));
        var (_, count) = EpgCoverageFacts.EncodeIntervals(many.Where(i => i.StartUtc < Now.AddHours(170)), Now);
        Assert.IsLessThanOrEqualTo(400, count);
    }

    [TestMethod]
    public async Task StageFromCatalogue_StoresFactsForParsedChannels_AndClearsChannelsTheSourceNoLongerHas()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await h.SeedSourceAsync();

        var first = Catalogue(("a", Now.AddHours(-1), Now.AddHours(5)), ("b", Now.AddHours(-1), Now.AddHours(5)));
        await using (var db = h.NewContext())
        {
            await NotificationTestFactories.Facts(db, timeProvider: h.Time).StageFromCatalogueAsync("src-1", first, h.Time.UtcNow, "r1", CancellationToken.None);
            await db.SaveChangesAsync();
        }

        var second = Catalogue(("a", Now.AddHours(-1), Now.AddHours(8)));
        await using (var db = h.NewContext())
        {
            await NotificationTestFactories.Facts(db, timeProvider: h.Time).StageFromCatalogueAsync("src-1", second, h.Time.UtcNow, "r2", CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var verify = h.NewContext();
        var rows = await verify.EpgNotificationCoverage.ToDictionaryAsync(x => x.XmltvChannelId);
        Assert.AreEqual(1, rows["a"].IntervalCount);
        Assert.AreEqual("r2", rows["a"].EvidenceRevision);
        Assert.AreEqual(0, rows["b"].IntervalCount, "A channel the source dropped no longer counts as covered.");
        Assert.IsTrue(rows.Values.All(r => !r.IsRelevant), "Staging facts never decides relevance.");
    }

    [TestMethod]
    public async Task Relevance_CountsDistinctMappedChannelsOnce_IncludesMappedChannelsMissingFromTheSource_AndExcludesUnmapped()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await h.SeedSourceAsync();
        await h.SetCoverageAsync("src-1", ("shared", [h.Interval(-1, 20)]), ("unmapped", [h.Interval(-1, 20)]));

        await h.PublishMappingsAsync("src-1", "shared", "absent-from-xmltv");
        await using (var db = h.NewContext())
        {
            db.Profiles.Add(new Profile
            {
                ProfileId = "profile-2", Name = "Kids", Enabled = true, OutputName = "kids", MergeMode = "single",
                CreatedUtc = h.Time.UtcNow, UpdatedUtc = h.Time.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // A second profile publishing the same XMLTV channel must not double count it.
        await h.PublishMappingsForProfileAsync("profile-2", "src-1", ["shared"]);

        await h.RunAsync();

        await using var verify = h.NewContext();
        var relevant = await verify.EpgNotificationCoverage.Where(x => x.IsRelevant).OrderBy(x => x.XmltvChannelId).ToListAsync();
        CollectionAssert.AreEqual(new[] { "absent-from-xmltv", "shared" }, relevant.Select(x => x.XmltvChannelId).ToArray());
        Assert.AreEqual(0, relevant[0].IntervalCount, "A mapped channel the source lacks is relevant and uncovered.");
        StringAssert.Contains(relevant[1].RelevanceContext, "Main");
        StringAssert.Contains(relevant[1].RelevanceContext, "Kids");
        Assert.IsFalse((await verify.EpgNotificationCoverage.SingleAsync(x => x.XmltvChannelId == "unmapped")).IsRelevant);
    }

    [TestMethod]
    public async Task Backfill_RebuildsFactsFromTheCacheOnce_WhenRelevantChannelsHaveNone()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"m3u-backfill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dataDir, "epg-cache"));
        try
        {
            var start = Now.UtcDateTime.AddHours(-1);
            var xml = "<?xml version=\"1.0\"?><tv><channel id=\"ch1\"><display-name>One</display-name></channel>" +
                      $"<programme start=\"{start:yyyyMMddHHmmss} +0000\" stop=\"{start.AddHours(10):yyyyMMddHHmmss} +0000\" channel=\"ch1\"><title>Show</title></programme></tv>";
            await File.WriteAllTextAsync(Path.Combine(dataDir, "epg-cache", "src-1.xml"), xml);

            await using var h = await NotificationLifecycleHarness.CreateAsync();
            await h.SeedSourceAsync();
            await h.PublishMappingsAsync("src-1", "ch1");
            var state = new NotificationRuntimeState();

            for (var pass = 0; pass < 2; pass++)
            {
                await using var db = h.NewContext();
                var facts = NotificationTestFactories.Facts(db, state, dataDir, h.Time);
                await facts.RefreshRelevanceAsync(force: false, CancellationToken.None);
                await facts.BackfillMissingFromCacheAsync(CancellationToken.None);
            }

            await using var verify = h.NewContext();
            var row = await verify.EpgNotificationCoverage.SingleAsync();
            Assert.IsTrue(row.IsRelevant);
            Assert.AreEqual(1, row.IntervalCount);
            Assert.AreEqual("cache-backfill", row.EvidenceRevision);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    private static EpgCatalogue Catalogue(params (string Channel, DateTimeOffset Start, DateTimeOffset Stop)[] programmes)
    {
        var byChannel = programmes.GroupBy(p => p.Channel).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<EpgProgrammeRecord>)g.Select(p => new EpgProgrammeRecord("src-1", p.Channel, p.Start, p.Stop, "t", null, null, [], [], null)).ToList());
        var channels = byChannel.Keys.Select(k => new EpgChannelRecord("src-1", k, k, null)).ToList();
        return new EpgCatalogue("src-1", channels, byChannel);
    }
}
