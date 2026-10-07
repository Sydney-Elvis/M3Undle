using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

[TestClass]
public sealed class NotificationPersistenceTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task Seeding_CreatesDisabledSettingsDestinationsAndOffRoutes_AndIsIdempotent()
    {
        await using var database = await MigratedDatabase.CreateAsync();
        await using (var db = database.CreateDbContext())
        {
            await NotificationConfigurationService.EnsureSeededAsync(db, Now, CancellationToken.None);
        }
        await using (var db = database.CreateDbContext())
        {
            await NotificationConfigurationService.EnsureSeededAsync(db, Now, CancellationToken.None);
        }

        await using var verify = database.CreateDbContext();
        var settings = await verify.NotificationSettings.SingleAsync();
        Assert.IsFalse(settings.SendingEnabled, "Upgrade and new installs must not send.");
        Assert.IsFalse(settings.Paused);

        var destinations = await verify.NotificationDestinations.ToListAsync();
        Assert.HasCount(2, destinations);
        Assert.IsTrue(destinations.All(d => !d.Enabled));
        CollectionAssert.AreEquivalent(
            new[] { NotificationProviderKinds.Matrix, NotificationProviderKinds.Smtp },
            destinations.Select(d => d.Kind).ToArray());

        var routes = await verify.NotificationRoutes.ToListAsync();
        Assert.HasCount(NotificationCatalog.Definitions.Count, routes);
        Assert.IsTrue(routes.All(r => r.DestinationId is null), "Every route defaults to Off.");
    }

    [TestMethod]
    public async Task ActiveIncident_IsUniquePerConditionAndSubject_ButResolvedHistoryAndOtherSubjectsAreAllowed()
    {
        await using var database = await MigratedDatabase.CreateAsync();
        await using var db = database.CreateDbContext();

        db.NotificationIncidents.Add(Incident("i1", "src-a", NotificationIncidentStates.Resolved, generation: 1));
        db.NotificationIncidents.Add(Incident("i2", "src-a", NotificationIncidentStates.Active, generation: 2));
        db.NotificationIncidents.Add(Incident("i3", "src-b", NotificationIncidentStates.Active, generation: 1));
        await db.SaveChangesAsync();

        db.NotificationIncidents.Add(Incident("i4", "src-a", NotificationIncidentStates.Active, generation: 3));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task OccurrenceKey_IsUnique_ButSeparateReminderSequencesAreAllowed()
    {
        await using var database = await MigratedDatabase.CreateAsync();
        await using var db = database.CreateDbContext();

        db.NotificationOccurrences.Add(Occurrence("o1", "epg.fetch_failed:i1:g1:reminder:1", NotificationOccurrenceKinds.Reminder, 1));
        db.NotificationOccurrences.Add(Occurrence("o2", "epg.fetch_failed:i1:g1:reminder:2", NotificationOccurrenceKinds.Reminder, 2));
        await db.SaveChangesAsync();

        db.NotificationOccurrences.Add(Occurrence("o3", "epg.fetch_failed:i1:g1:reminder:1", NotificationOccurrenceKinds.Reminder, 1));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task Delivery_IsUniquePerOccurrenceDestinationTargetAndIdentityRevision()
    {
        await using var database = await MigratedDatabase.CreateAsync();
        string destinationId;
        await using (var db = database.CreateDbContext())
        {
            await NotificationConfigurationService.EnsureSeededAsync(db, Now, CancellationToken.None);
            destinationId = (await db.NotificationDestinations.FirstAsync(d => d.Kind == NotificationProviderKinds.Smtp)).DestinationId;
            db.NotificationOccurrences.Add(Occurrence("o1", "k1", NotificationOccurrenceKinds.OneTime, 0));
            await db.SaveChangesAsync();
        }

        await using var write = database.CreateDbContext();
        write.NotificationDeliveries.Add(Delivery("d1", "o1", destinationId, "rcpt-1", identityRevision: 1));
        write.NotificationDeliveries.Add(Delivery("d2", "o1", destinationId, "rcpt-2", identityRevision: 1));
        write.NotificationDeliveries.Add(Delivery("d3", "o1", destinationId, "rcpt-1", identityRevision: 2));
        await write.SaveChangesAsync();

        write.NotificationDeliveries.Add(Delivery("d4", "o1", destinationId, "rcpt-1", identityRevision: 1));
        await Assert.ThrowsAsync<DbUpdateException>(() => write.SaveChangesAsync());
    }

    [TestMethod]
    public async Task Observations_GetMonotonicIds_AndSurviveRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"notif-{Guid.NewGuid():N}.db");
        try
        {
            await using (var first = await MigratedDatabase.CreateAsync(path))
            {
                await using var db = first.CreateDbContext();
                var writer = new NotificationOccurrenceWriter(db, TimeProvider.System);
                writer.StageObservation(NotificationEvidenceKeys.EpgSourceCheck, NotificationSubjectKinds.EpgSource, "src", "failed", Now, "http");
                writer.StageObservation(NotificationEvidenceKeys.EpgSourceCheck, NotificationSubjectKinds.EpgSource, "src", "ok", Now.AddMinutes(-5));
                await db.SaveChangesAsync();
            }

            SqliteClearPools();

            await using var second = await MigratedDatabase.CreateAsync(path);
            await using var verify = second.CreateDbContext();
            var rows = await verify.NotificationConditionObservations.OrderBy(x => x.ObservationId).ToListAsync();
            Assert.HasCount(2, rows);
            Assert.IsLessThan(rows[1].ObservationId, rows[0].ObservationId, "Ordering is by database-generated id, not wall-clock time.");
            Assert.AreEqual("failed", rows[0].Outcome);
            Assert.IsFalse(rows[0].Consumed);
        }
        finally
        {
            SqliteClearPools();
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
                File.Delete(file);
        }
    }

    [TestMethod]
    public async Task OneTimeCapture_UsesPolicyAtCommit_NothingIsRetainedWhileOffDisabledOrPaused()
    {
        await using var database = await MigratedDatabase.CreateAsync();
        await using (var db = database.CreateDbContext())
            await NotificationConfigurationService.EnsureSeededAsync(db, Now, CancellationToken.None);

        var note = new OneTimeNotification(NotificationKeys.SystemRestarted, "boot-1", "Info", "Restarted", "body", null, null, Now);

        await using (var db = database.CreateDbContext())
        {
            var writer = new NotificationOccurrenceWriter(db, TimeProvider.System);
            Assert.IsNull(await writer.StageOneTimeAsync(note, CancellationToken.None), "Defaults must not capture.");
        }

        string destinationId;
        await using (var db = database.CreateDbContext())
        {
            destinationId = (await db.NotificationDestinations.FirstAsync(d => d.Kind == NotificationProviderKinds.Matrix)).DestinationId;
            (await db.NotificationSettings.SingleAsync()).SendingEnabled = true;
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateDbContext())
        {
            var writer = new NotificationOccurrenceWriter(db, TimeProvider.System);
            Assert.IsNull(await writer.StageOneTimeAsync(note, CancellationToken.None), "Route Off must not capture.");
        }

        await using (var db = database.CreateDbContext())
        {
            (await db.NotificationRoutes.SingleAsync(r => r.NotificationKey == NotificationKeys.SystemRestarted)).DestinationId = destinationId;
            (await db.NotificationSettings.SingleAsync()).Paused = true;
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateDbContext())
        {
            var writer = new NotificationOccurrenceWriter(db, TimeProvider.System);
            Assert.IsNull(await writer.StageOneTimeAsync(note, CancellationToken.None), "Paused must not capture.");
        }

        await using (var db = database.CreateDbContext())
        {
            (await db.NotificationSettings.SingleAsync()).Paused = false;
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateDbContext())
        {
            var writer = new NotificationOccurrenceWriter(db, TimeProvider.System);
            Assert.IsNotNull(await writer.StageOneTimeAsync(note, CancellationToken.None));
            Assert.IsNull(await writer.StageOneTimeAsync(note, CancellationToken.None), "The same occurrence key is captured once.");
            await db.SaveChangesAsync();
        }

        await using var verify = database.CreateDbContext();
        Assert.AreEqual(1, await verify.NotificationOccurrences.CountAsync());
    }

    [TestMethod]
    public async Task RecordingEpgOutcome_CommitsSourceFetchRunAndObservationTogether_AndRollsBackTogether()
    {
        await using var database = await MigratedDatabase.CreateAsync();
        await using (var setup = database.CreateDbContext())
        {
            setup.EpgSources.Add(new EpgSource
            {
                EpgSourceId = "src-1", Name = "Guide", Kind = "xmltv_url", CreatedUtc = Now, UpdatedUtc = Now,
            });
            await setup.SaveChangesAsync();
        }

        var failed = new M3Undle.Web.Application.Epg.EpgSourceFetcher.FetchResult(null, "fail", 0, null, null, "HTTP fetch failed: secret-host");

        await using (var db = database.CreateDbContext())
        {
            var recorder = NewRecorder(db);
            var source = await db.EpgSources.AsNoTracking().SingleAsync();
            await recorder.RecordAsync(source, failed, M3Undle.Core.Epg.EpgCatalogue.Empty("src-1"), Now, CancellationToken.None);
        }

        await using (var verify = database.CreateDbContext())
        {
            var source = await verify.EpgSources.SingleAsync();
            Assert.IsNotNull(source.LastFailureUtc);
            Assert.AreEqual(1, await verify.EpgFetchRuns.CountAsync());
            var observation = await verify.NotificationConditionObservations.SingleAsync();
            Assert.AreEqual("src-1", observation.SubjectId);
            Assert.AreEqual(NotificationObservationOutcomes.Failed, observation.Outcome);
            Assert.AreEqual("http", observation.SafeDetail, "Observations carry a failure class, never upstream text.");
        }

        // A commit that fails (fetch run for a source that does not exist violates its foreign key) must leave no
        // observation behind either.
        await using (var db = database.CreateDbContext())
        {
            var recorder = NewRecorder(db);
            var ghost = new EpgSource { EpgSourceId = "ghost", Name = "Ghost", CreatedUtc = Now, UpdatedUtc = Now };
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                recorder.RecordAsync(ghost, failed, M3Undle.Core.Epg.EpgCatalogue.Empty("ghost"), Now, CancellationToken.None));
        }

        await using var final = database.CreateDbContext();
        Assert.AreEqual(1, await final.NotificationConditionObservations.CountAsync(), "The failed commit rolled back its observation.");
        Assert.AreEqual(1, await final.EpgFetchRuns.CountAsync());
    }

    [TestMethod]
    public async Task CacheReuse_StagesNoObservation()
    {
        await using var database = await MigratedDatabase.CreateAsync();
        await using (var setup = database.CreateDbContext())
        {
            setup.EpgSources.Add(new EpgSource { EpgSourceId = "src-1", Name = "Guide", CreatedUtc = Now, UpdatedUtc = Now });
            await setup.SaveChangesAsync();
        }

        await using var db = database.CreateDbContext();
        var source = await db.EpgSources.AsNoTracking().SingleAsync();
        var reused = M3Undle.Web.Application.Epg.EpgSourceFetcher.FetchResult.CacheReused(null, null);
        await NewRecorder(db).RecordAsync(source, reused, M3Undle.Core.Epg.EpgCatalogue.Empty("src-1"), Now, CancellationToken.None);

        await using var verify = database.CreateDbContext();
        Assert.AreEqual(0, await verify.NotificationConditionObservations.CountAsync());
    }

    private static M3Undle.Web.Application.Epg.EpgSourceOutcomeRecorder NewRecorder(M3Undle.Web.Data.ApplicationDbContext db) =>
        new(db, new NotificationOccurrenceWriter(db, TimeProvider.System), NotificationTestFactories.Facts(db), new Stubs.NullEventService(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<M3Undle.Web.Application.Epg.EpgSourceOutcomeRecorder>.Instance);

    private static void SqliteClearPools() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private static NotificationIncident Incident(string id, string subject, string state, int generation) => new()
    {
        IncidentId = id,
        NotificationKey = NotificationKeys.EpgFetchFailed,
        SubjectKind = NotificationSubjectKinds.EpgSource,
        SubjectId = subject,
        Generation = generation,
        State = state,
        FirstUnhealthyUtc = Now,
        LastObservedUtc = Now,
        UpdatedUtc = Now,
    };

    private static NotificationOccurrence Occurrence(string id, string key, string kind, int sequence) => new()
    {
        OccurrenceId = id,
        OccurrenceKey = key,
        NotificationKey = NotificationKeys.EpgFetchFailed,
        Kind = kind,
        Sequence = sequence,
        Title = "t",
        Body = "b",
        OccurredUtc = Now,
        CreatedUtc = Now,
    };

    private static NotificationDelivery Delivery(string id, string occurrenceId, string destinationId, string target, int identityRevision) => new()
    {
        DeliveryId = id,
        OccurrenceId = occurrenceId,
        DestinationId = destinationId,
        ProviderKind = NotificationProviderKinds.Smtp,
        TargetId = target,
        TargetLabel = target,
        DeliveryIdentityRevision = identityRevision,
        DueUtc = Now,
        PayloadTitle = "t",
        PayloadBody = "b",
        MessageId = $"<{id}@m3undle>",
        CreatedUtc = Now,
        UpdatedUtc = Now,
    };
}
