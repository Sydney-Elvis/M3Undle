using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

[TestClass]
public sealed class NotificationDeliveryProcessorTests
{
    private static async Task<(NotificationLifecycleHarness Harness, string DeliveryId)> OpenedAsync(
        string kind = NotificationProviderKinds.Matrix, bool fileBacked = false, string key = NotificationKeys.EpgFetchFailed)
    {
        var h = await NotificationLifecycleHarness.CreateAsync(fileBacked: fileBacked);
        await h.SeedSourceAsync();
        await h.EnableAsync(kind, key);
        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();
        var id = (await h.DeliveriesAsync()).First().Delivery.DeliveryId;
        return (h, id);
    }

    [TestMethod]
    public async Task Accepted_RecordsTheResultAndTheTargetAcceptance_ThenRecoveryFollows()
    {
        var (h, id) = await OpenedAsync();
        await using var _ = h;
        h.Matrix.Then(NotificationSendResult.Accepted("$event1"));

        Assert.IsTrue(await h.ProcessAsync(NotificationProviderKinds.Matrix));

        var delivery = await h.DeliveryAsync(id);
        Assert.AreEqual(NotificationDeliveryStates.Accepted, delivery.State);
        Assert.AreEqual("$event1", delivery.RemoteReference);
        Assert.AreEqual(1, delivery.AttemptCount);
        Assert.IsNull(delivery.ClaimOwner);
        Assert.AreEqual(delivery.MessageId, h.Matrix.Requests.Single().MessageId, "The stable message identity travels with every attempt.");

        await using (var db = h.NewContext())
        {
            var target = await db.NotificationIncidentTargets.SingleAsync();
            Assert.IsNotNull(target.OpeningAcceptedUtc);
            Assert.IsFalse(target.OpeningUncertain);
        }

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Ok);
        await h.RunAsync();
        Assert.IsTrue(await h.ProcessAsync(NotificationProviderKinds.Matrix));
        Assert.AreEqual(2, h.Matrix.Calls);
        StringAssert.StartsWith(h.Matrix.Requests[1].Message.Title, "Recovered");

        await using var verify = h.NewContext();
        Assert.IsNotNull((await verify.NotificationIncidentTargets.SingleAsync()).RecoveryAcceptedUtc);
    }

    [TestMethod]
    public async Task NothingDueMeansNothingHappens_AndPausedOrDisabledSendingNeverClaims()
    {
        var (h, id) = await OpenedAsync();
        await using var _ = h;

        await using (var db = h.NewContext())
        {
            (await db.NotificationSettings.SingleAsync()).Paused = true;
            await db.SaveChangesAsync();
        }

        Assert.IsFalse(await h.ProcessAsync(NotificationProviderKinds.Matrix), "Pause prevents new claims.");
        Assert.AreEqual(0, h.Matrix.Calls);
        Assert.AreEqual(NotificationDeliveryStates.Pending, (await h.DeliveryAsync(id)).State);
        Assert.IsFalse(await h.ProcessAsync(NotificationProviderKinds.Smtp), "Another provider kind has nothing of its own.");
    }

    [TestMethod]
    public async Task RetryableFailure_BacksOffWithJitter_HonoursLongerServerGuidance_AndStopsAfterEightAttempts()
    {
        var (h, id) = await OpenedAsync();
        await using var _ = h;
        h.JitterValue = 0.5; // exactly the nominal delay

        h.Matrix.Then(NotificationSendResult.Retryable("rate_limited", "slow down"));
        await h.ProcessAsync(NotificationProviderKinds.Matrix);
        var first = await h.DeliveryAsync(id);
        Assert.AreEqual(NotificationDeliveryStates.RetryScheduled, first.State);
        Assert.AreEqual(1, first.AttemptCount);
        Assert.AreEqual(h.Time.UtcNow.AddSeconds(30), first.DueUtc, "First backoff is 30 seconds.");
        Assert.IsNull(first.TransportStartedUtc, "A known failure is not a possibly accepted send.");

        Assert.IsFalse(await h.ProcessAsync(NotificationProviderKinds.Matrix), "Not due yet, so no tight loop.");
        h.Time.Advance(TimeSpan.FromSeconds(31));
        h.Matrix.Then(NotificationSendResult.Retryable("rate_limited", "slow", TimeSpan.FromMinutes(10)));
        await h.ProcessAsync(NotificationProviderKinds.Matrix);
        var second = await h.DeliveryAsync(id);
        Assert.AreEqual(h.Time.UtcNow.AddMinutes(10), second.DueUtc, "A longer Retry-After wins over the computed delay.");

        for (var attempt = 3; attempt <= 8; attempt++)
        {
            h.Time.Advance(TimeSpan.FromHours(1));
            h.Matrix.Then(NotificationSendResult.Retryable("unavailable", "down"));
            await h.ProcessAsync(NotificationProviderKinds.Matrix);
        }

        var final = await h.DeliveryAsync(id);
        Assert.AreEqual(NotificationDeliveryStates.Failed, final.State);
        Assert.AreEqual(8, final.AttemptCount);
        Assert.AreEqual("unavailable", final.ErrorCode);

        h.Time.Advance(TimeSpan.FromDays(1));
        Assert.IsFalse(await h.ProcessAsync(NotificationProviderKinds.Matrix), "A failed cycle never retries on its own.");
    }

    [TestMethod]
    public void BackoffPolicy_IsExponentialCappedJitteredAndHonoursGuidance()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(30), NotificationRetryPolicy.NextDelay(1, null, 0.5));
        Assert.AreEqual(TimeSpan.FromSeconds(60), NotificationRetryPolicy.NextDelay(2, null, 0.5));
        Assert.AreEqual(TimeSpan.FromSeconds(24), NotificationRetryPolicy.NextDelay(1, null, 0.0), "−20% jitter");
        Assert.AreEqual(TimeSpan.FromSeconds(36), NotificationRetryPolicy.NextDelay(1, null, 1.0), "+20% jitter");
        Assert.AreEqual(TimeSpan.FromMinutes(30), NotificationRetryPolicy.NextDelay(8, null, 0.5), "Capped at 30 minutes.");
        Assert.AreEqual(TimeSpan.FromMinutes(30), NotificationRetryPolicy.NextDelay(30, null, 1.0), "The cap holds even with jitter.");
        Assert.AreEqual(TimeSpan.FromHours(2), NotificationRetryPolicy.NextDelay(1, TimeSpan.FromHours(2), 0.5));
        Assert.AreEqual(TimeSpan.FromSeconds(30), NotificationRetryPolicy.NextDelay(1, TimeSpan.FromSeconds(1), 0.5), "Shorter guidance never shortens the backoff.");
    }

    [TestMethod]
    public async Task PermanentFailure_StopsAutomaticRetries_AndExplicitRetryStartsANewRecordedCycleOnTheSameDelivery()
    {
        var (h, id) = await OpenedAsync();
        await using var _ = h;
        h.Matrix.Then(NotificationSendResult.Permanent("auth_failed", "token rejected"));
        await h.ProcessAsync(NotificationProviderKinds.Matrix);

        var failed = await h.DeliveryAsync(id);
        Assert.AreEqual(NotificationDeliveryStates.Failed, failed.State);
        h.Time.Advance(TimeSpan.FromDays(1));
        Assert.IsFalse(await h.ProcessAsync(NotificationProviderKinds.Matrix));

        Assert.AreEqual(DeliveryActionStatus.Conflict, (await h.RetryAsync(id, acknowledge: false, expectedRevision: failed.Revision - 1)).Status, "Stale revisions conflict.");
        Assert.AreEqual(DeliveryActionStatus.Ok, (await h.RetryAsync(id, acknowledge: false)).Status);

        var retried = await h.DeliveryAsync(id);
        Assert.AreEqual(NotificationDeliveryStates.Pending, retried.State);
        Assert.AreEqual(2, retried.CycleNumber);
        Assert.AreEqual(0, retried.AttemptCount);
        Assert.AreEqual(id, retried.DeliveryId, "A retry reuses the same logical delivery; it never retargets.");

        await h.ProcessAsync(NotificationProviderKinds.Matrix);
        Assert.AreEqual(NotificationDeliveryStates.Accepted, (await h.DeliveryAsync(id)).State);
        Assert.AreEqual(2, h.Matrix.Calls);
        Assert.AreEqual(h.Matrix.Requests[0].MessageId, h.Matrix.Requests[1].MessageId);
    }

    [TestMethod]
    public async Task UncertainSend_IsNeverRetriedAutomatically_NeedsAcknowledgementToRetry_AndOwesNoRecovery()
    {
        var (h, id) = await OpenedAsync(NotificationProviderKinds.Smtp);
        await using var _ = h;
        var deliveries = await h.DeliveriesAsync();
        Assert.HasCount(2, deliveries);

        h.Smtp.Then(NotificationSendResult.Uncertain("ack_lost", "connection dropped before the final reply"));
        await h.ProcessAsync(NotificationProviderKinds.Smtp);
        var uncertain = deliveries.Select(d => d.Delivery.DeliveryId).Select(async x => await h.DeliveryAsync(x)).Select(t => t.Result)
            .Single(d => d.State == NotificationDeliveryStates.Uncertain);

        h.Time.Advance(TimeSpan.FromDays(1));
        await h.ProcessAsync(NotificationProviderKinds.Smtp); // processes the other recipient, not the uncertain one
        Assert.AreEqual(NotificationDeliveryStates.Uncertain, (await h.DeliveryAsync(uncertain.DeliveryId)).State);

        await using (var db = h.NewContext())
        {
            var target = await db.NotificationIncidentTargets.SingleAsync(t => t.TargetId == uncertain.TargetId);
            Assert.IsTrue(target.OpeningUncertain);
            Assert.IsNull(target.OpeningAcceptedUtc);
        }

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Ok);
        await h.RunAsync();
        var recoveryTargets = (await h.DeliveriesAsync()).Where(d => d.Occurrence.Kind == NotificationOccurrenceKinds.Recovery).Select(d => d.Delivery.TargetId).ToList();
        CollectionAssert.DoesNotContain(recoveryTargets, uncertain.TargetId, "No unconditional recovery for an opening whose acceptance is unknown.");

        var blocked = await h.RetryAsync(uncertain.DeliveryId, acknowledge: false);
        Assert.AreEqual(DeliveryActionStatus.Invalid, blocked.Status);
        StringAssert.Contains(blocked.Message, "twice");
        Assert.AreEqual(DeliveryActionStatus.Ok, (await h.RetryAsync(uncertain.DeliveryId, acknowledge: true)).Status);
    }

    [TestMethod]
    public async Task Dismiss_OnlyAppliesToFailedOrUncertain_AndDoesNotTouchIncidentHealth()
    {
        var (h, id) = await OpenedAsync();
        await using var _ = h;
        Assert.AreEqual(DeliveryActionStatus.Conflict, (await h.DismissAsync(id)).Status, "Pending work cannot be dismissed.");

        h.Matrix.Then(NotificationSendResult.Permanent("rejected", "no"));
        await h.ProcessAsync(NotificationProviderKinds.Matrix);
        Assert.AreEqual(DeliveryActionStatus.Ok, (await h.DismissAsync(id)).Status);

        var dismissed = await h.DeliveryAsync(id);
        Assert.AreEqual(NotificationDeliveryStates.Dismissed, dismissed.State);
        Assert.IsNotNull(dismissed.DismissedUtc);
        Assert.AreEqual(NotificationIncidentStates.Active, (await h.IncidentsAsync()).Single().State);
    }

    [TestMethod]
    public async Task ExpiredClaim_WithoutTransportStart_ReturnsToPending_SoNothingIsLost()
    {
        var (h, id) = await OpenedAsync();
        await using var _ = h;
        await using (var db = h.NewContext())
        {
            var d = await db.NotificationDeliveries.SingleAsync();
            d.State = NotificationDeliveryStates.Claimed;
            d.ClaimOwner = "crashed";
            d.ClaimExpiresUtc = h.Time.UtcNow.AddMinutes(2);
            await db.SaveChangesAsync();
        }

        Assert.AreEqual(0, await h.RecoverExpiredAsync(), "The lease has not expired.");
        h.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.AreEqual(1, await h.RecoverExpiredAsync());

        var recovered = await h.DeliveryAsync(id);
        Assert.AreEqual(NotificationDeliveryStates.Pending, recovered.State);
        Assert.IsNull(recovered.ClaimOwner);
        await h.ProcessAsync(NotificationProviderKinds.Matrix);
        Assert.AreEqual(NotificationDeliveryStates.Accepted, (await h.DeliveryAsync(id)).State);
    }

    [TestMethod]
    public async Task ExpiredClaim_AfterTransportStart_IsUncertainForSmtp_ButRetriedForAnIdempotentTransport()
    {
        foreach (var (kind, expected) in new[]
        {
            (NotificationProviderKinds.Smtp, NotificationDeliveryStates.Uncertain),
            (NotificationProviderKinds.Matrix, NotificationDeliveryStates.RetryScheduled),
        })
        {
            var (h, id) = await OpenedAsync(kind);
            await using var _ = h;
            await using (var db = h.NewContext())
            {
                var d = await db.NotificationDeliveries.FirstAsync(x => x.DeliveryId == id);
                d.State = NotificationDeliveryStates.Claimed;
                d.ClaimOwner = "crashed";
                d.ClaimExpiresUtc = h.Time.UtcNow.AddMinutes(1);
                d.TransportStartedUtc = h.Time.UtcNow;
                d.AttemptCount = 1;
                await db.SaveChangesAsync();
            }

            h.Time.Advance(TimeSpan.FromMinutes(3));
            await h.RecoverExpiredAsync();
            Assert.AreEqual(expected, (await h.DeliveryAsync(id)).State, kind);
        }
    }

    [TestMethod]
    public async Task HostShutdownDuringSend_IsNeverMarkedProcessed_AndFollowsTheTransportBoundary()
    {
        foreach (var (kind, provider, expected) in new[]
        {
            (NotificationProviderKinds.Smtp, "smtp", NotificationDeliveryStates.Uncertain),
            (NotificationProviderKinds.Matrix, "matrix", NotificationDeliveryStates.RetryScheduled),
        })
        {
            var (h, id) = await OpenedAsync(kind);
            await using var _ = h;
            using var cts = new CancellationTokenSource();
            var scripted = provider == "smtp" ? h.Smtp : h.Matrix;
            scripted.Then(async (_, token) =>
            {
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
                return NotificationSendResult.Accepted();
            });

            await Assert.ThrowsAsync<OperationCanceledException>(() => h.ProcessAsync(kind, hostToken: cts.Token));

            var delivery = await h.DeliveryAsync(id);
            Assert.AreEqual(expected, delivery.State, kind);
            Assert.AreNotEqual(NotificationDeliveryStates.Accepted, delivery.State);
            Assert.IsNull(delivery.ClaimOwner);
        }
    }

    [TestMethod]
    public async Task RevisionChangeBeforeClaim_SuppressesInsteadOfSending_AndNeverSendsToAChangedRecipient()
    {
        var (h, id) = await OpenedAsync(NotificationProviderKinds.Smtp);
        await using var _ = h;

        await using (var db = h.NewContext())
        {
            var destination = await db.NotificationDestinations.SingleAsync(d => d.Kind == NotificationProviderKinds.Smtp);
            destination.ConfigRevision++;
            destination.DeliveryIdentityRevision++;
            destination.VerifiedRevision = null; // an edit invalidates the exact-revision verification
            await db.SaveChangesAsync();
        }

        while (await h.ProcessAsync(NotificationProviderKinds.Smtp)) { }

        Assert.AreEqual(0, h.Smtp.Calls);
        var all = await h.DeliveriesAsync();
        Assert.IsTrue(all.All(d => d.Delivery.State == NotificationDeliveryStates.Suppressed));
        Assert.IsTrue(all.All(d => d.Delivery.SuppressedReason == NotificationSuppressionReasons.ConfigChanged));
        Assert.IsNotNull(await h.DeliveryAsync(id));
    }

    [TestMethod]
    public async Task ReminderAndRecoveryToggles_AreCheckedAgainBeforeClaiming()
    {
        var (h, _) = await OpenedAsync();
        await using var _h = h;
        await h.ProcessAsync(NotificationProviderKinds.Matrix);
        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Ok);
        await h.RunAsync();

        await using (var db = h.NewContext())
        {
            (await db.NotificationRoutes.SingleAsync(r => r.NotificationKey == NotificationKeys.EpgFetchFailed)).SendRecovery = false;
            await db.SaveChangesAsync();
        }

        while (await h.ProcessAsync(NotificationProviderKinds.Matrix)) { }
        Assert.AreEqual(1, h.Matrix.Calls, "The queued recovery is dropped once recovery notices are switched off.");
    }

    [TestMethod]
    public async Task OrdinaryRestart_PreservesPendingWork_AndTheNewProcessSendsIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"notif-restart-{Guid.NewGuid():N}.db");
        try
        {
            string id;
            await using (var first = await NotificationLifecycleHarness.CreateAsync(databasePath: path))
            {
                await first.SeedSourceAsync();
                await first.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgFetchFailed);
                await first.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
                first.Time.Advance(TimeSpan.FromMinutes(11));
                await first.RunAsync();
                id = (await first.DeliveriesAsync()).Single().Delivery.DeliveryId;
                Assert.AreEqual(0, first.Matrix.Calls, "The first process stops before sending.");
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await using var second = await NotificationLifecycleHarness.CreateAsync(databasePath: path);
            second.Time.Advance(TimeSpan.FromMinutes(12));
            Assert.AreEqual(NotificationDeliveryStates.Pending, (await second.DeliveryAsync(id)).State, "A restart is not a restore: live pending work survives.");
            await second.ProcessAsync(NotificationProviderKinds.Matrix);
            Assert.AreEqual(NotificationDeliveryStates.Accepted, (await second.DeliveryAsync(id)).State);

            await second.RunAsync();
            Assert.HasCount(1, await second.DeliveriesAsync(), "Startup reconciliation does not duplicate the queued opening.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
                File.Delete(file);
        }
    }

    [TestMethod]
    public async Task TwoWorkersRacingForTheSameDelivery_SendItExactlyOnce()
    {
        var (h, id) = await OpenedAsync(fileBacked: true);
        await using var _ = h;
        h.Matrix.Then(async (_, token) =>
        {
            await Task.Delay(150, token);
            return NotificationSendResult.Accepted();
        });

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => Task.Run(() => h.ProcessAsync(NotificationProviderKinds.Matrix, owner: $"worker-{i}"))));

        Assert.AreEqual(1, h.Matrix.Calls, "Atomic claims allow one winner; losers do not send.");
        Assert.AreEqual(NotificationDeliveryStates.Accepted, (await h.DeliveryAsync(id)).State);
        Assert.IsGreaterThanOrEqualTo(1, results.Count(r => r));
    }

    [TestMethod]
    public async Task ASlowTransport_DoesNotBlockAnotherProvidersQueue()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync(fileBacked: true);
        await h.SeedSourceAsync();
        await h.EnableAsync(NotificationProviderKinds.Smtp, NotificationKeys.EpgFetchFailed);
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgRefreshOverdue);

        await using (var db = h.NewContext())
        {
            // Hand-build one pending delivery per transport so both queues have work.
            var smtp = await db.NotificationDestinations.SingleAsync(d => d.Kind == NotificationProviderKinds.Smtp);
            var matrix = await db.NotificationDestinations.SingleAsync(d => d.Kind == NotificationProviderKinds.Matrix);
            db.NotificationOccurrences.AddRange(
                Occ("o-smtp", NotificationKeys.EpgFetchFailed, h.Time.UtcNow), Occ("o-matrix", NotificationKeys.EpgRefreshOverdue, h.Time.UtcNow));
            await db.SaveChangesAsync();
            db.NotificationDeliveries.AddRange(
                Del("d-smtp", "o-smtp", smtp, "rcpt-0", h.Time.UtcNow), Del("d-matrix", "o-matrix", matrix, "matrix-room", h.Time.UtcNow));
            await db.SaveChangesAsync();
        }

        var gate = new TaskCompletionSource();
        h.Smtp.Then(async (_, _) =>
        {
            await gate.Task;
            return NotificationSendResult.Accepted();
        });

        var slow = Task.Run(() => h.ProcessAsync(NotificationProviderKinds.Smtp));
        for (var i = 0; i < 500 && h.Smtp.Calls == 0; i++)
            await Task.Delay(10);
        Assert.AreEqual(1, h.Smtp.Calls, "SMTP must be mid-send before the Matrix check.");
        Assert.IsTrue(await h.ProcessAsync(NotificationProviderKinds.Matrix), "Matrix progresses while SMTP is stuck mid-send.");
        Assert.AreEqual(NotificationDeliveryStates.Accepted, (await h.DeliveryAsync("d-matrix")).State);
        Assert.AreEqual(NotificationDeliveryStates.Claimed, (await h.DeliveryAsync("d-smtp")).State);

        gate.SetResult();
        await slow;
        Assert.AreEqual(NotificationDeliveryStates.Accepted, (await h.DeliveryAsync("d-smtp")).State);
    }

    [TestMethod]
    public async Task ThirdProvider_IsDispatchedThroughRegistrationAlone()
    {
        var pushover = new ScriptedProvider("pushover");
        await using var h = await NotificationLifecycleHarness.CreateAsync(extraProviders: [pushover], extraAdapters: [new PushoverAdapter()]);
        await h.SeedSourceAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix);

        await using (var db = h.NewContext())
        {
            db.NotificationDestinations.Add(new NotificationDestination
            {
                DestinationId = "dest-push", Kind = "pushover", Enabled = true, ConfigRevision = 1, VerifiedRevision = 1,
                CreatedUtc = h.Time.UtcNow, UpdatedUtc = h.Time.UtcNow,
            });
            await db.SaveChangesAsync();
            (await db.NotificationRoutes.SingleAsync(r => r.NotificationKey == NotificationKeys.EpgFetchFailed)).DestinationId = "dest-push";
            await db.SaveChangesAsync();
        }

        // Producers, incident lifecycle, routing and the worker are exactly the production code; only the provider and
        // its adapter were registered.
        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();
        Assert.IsTrue(await h.ProcessAsync("pushover"));

        var request = pushover.Requests.Single();
        Assert.AreEqual("push-device", request.Target.TargetId);
        Assert.IsInstanceOfType<PushoverConfiguration>(request.Configuration);
        Assert.AreEqual(NotificationDeliveryStates.Accepted, (await h.DeliveriesAsync()).Single().Delivery.State);
        Assert.AreEqual(0, h.Matrix.Calls);
    }

    private sealed record PushoverConfiguration() : NotificationProviderConfiguration("pushover");

    private sealed class PushoverAdapter : INotificationDestinationAdapter
    {
        public string Kind => "pushover";
        public bool IsStructurallyComplete(NotificationDestination destination) => true;
        public IReadOnlyList<NotificationTarget> GetTargets(NotificationDestination destination) => [new NotificationTarget("push-device", "Phone")];
        public NotificationProviderConfiguration? ResolveConfiguration(NotificationDestination destination) => new PushoverConfiguration();
    }

    private static NotificationOccurrence Occ(string id, string key, DateTime now) => new()
    {
        OccurrenceId = id, OccurrenceKey = id, NotificationKey = key, Kind = NotificationOccurrenceKinds.OneTime,
        Title = "t", Body = "b", Severity = "Warning", OccurredUtc = now, CreatedUtc = now,
    };

    private static NotificationDelivery Del(string id, string occurrenceId, NotificationDestination destination, string target, DateTime now) => new()
    {
        DeliveryId = id, OccurrenceId = occurrenceId, DestinationId = destination.DestinationId, ProviderKind = destination.Kind,
        TargetId = target, TargetLabel = target, DeliveryIdentityRevision = destination.DeliveryIdentityRevision,
        ConfigRevision = destination.ConfigRevision, RouteRevision = 1, DueUtc = now, PayloadTitle = "t", PayloadBody = "b",
        MessageId = $"<{id}@m3undle.local>", CreatedUtc = now, UpdatedUtc = now,
    };
}
