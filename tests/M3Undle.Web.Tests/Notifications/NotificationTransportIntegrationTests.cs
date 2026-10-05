using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Application.Notifications.Providers;
using M3Undle.Web.Data.Entities;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

// Real configuration service, real worker logic and the real SMTP and Matrix transports, against protocol-level fakes.
// Mutates process-wide encryption key variables, so it must not run alongside other tests.
[TestClass]
[DoNotParallelize]
public sealed class NotificationTransportIntegrationTests
{
    private sealed class KeyScope : IDisposable
    {
        private readonly string? _previousKey = Environment.GetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY");
        private readonly string? _previousKeys = Environment.GetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS");

        public KeyScope()
        {
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS", null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY", _previousKey);
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS", _previousKeys);
        }
    }

    private static SmtpNotificationProvider SmtpTrusting(FakeSmtpServer server) => new(NullLogger<SmtpNotificationProvider>.Instance, () =>
    {
        var client = new SmtpClient { CheckCertificateRevocation = false };
        client.ServerCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString() == server.Certificate.GetCertHashString();
        return client;
    });

    private static MatrixNotificationProvider MatrixProvider()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMatrixHttpClient();
        return new MatrixNotificationProvider(
            services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(),
            new LabRuntimeOptions(new M3Undle.Web.Application.EnvironmentVariableService(NullLogger<M3Undle.Web.Application.EnvironmentVariableService>.Instance), true),
            NullLogger<MatrixNotificationProvider>.Instance);
    }

    private static async Task<int> RevisionAsync(NotificationLifecycleHarness h, string kind)
    {
        await using var db = h.NewContext();
        return (await db.NotificationDestinations.AsNoTracking().SingleAsync(d => d.Kind == kind)).ConfigRevision;
    }

    private static async Task ConfigureSmtpAsync(NotificationLifecycleHarness h, FakeSmtpServer server)
    {
        var revision = await RevisionAsync(h, NotificationProviderKinds.Smtp);
        var saved = await h.Config.SaveSmtpAsync(
            new("127.0.0.1", server.Port, "starttls", "password", "mailer", "p@ss word ", false, "m3undle@example.org", "M3Undle", ["a@example.org", "b@example.org"]),
            revision, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Ok, saved.Status, saved.Message);
        var test = await h.Config.TestAsync(NotificationProviderKinds.Smtp, saved.Revision!.Value, CancellationToken.None);
        Assert.IsTrue(test.VerificationApplied, test.Summary);
        Assert.AreEqual(NotificationOperationStatus.Ok, (await h.Config.SetDestinationEnabledAsync(NotificationProviderKinds.Smtp, true, saved.Revision.Value, CancellationToken.None)).Status);
    }

    private static async Task ConfigureMatrixAsync(NotificationLifecycleHarness h, FakeMatrixHomeserver server)
    {
        var revision = await RevisionAsync(h, NotificationProviderKinds.Matrix);
        var saved = await h.Config.SaveMatrixAsync(new(server.BaseUrl, FakeMatrixHomeserver.RoomId, FakeMatrixHomeserver.Token, false, AllowInsecureHttp: true), revision, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Ok, saved.Status, saved.Message);
        var test = await h.Config.TestAsync(NotificationProviderKinds.Matrix, saved.Revision!.Value, CancellationToken.None);
        Assert.IsTrue(test.VerificationApplied, test.Summary);
        Assert.AreEqual(NotificationOperationStatus.Ok, (await h.Config.SetDestinationEnabledAsync(NotificationProviderKinds.Matrix, true, saved.Revision.Value, CancellationToken.None)).Status);
    }

    private static async Task RouteAsync(NotificationLifecycleHarness h, string key, string kind)
    {
        await using var db = h.NewContext();
        var revision = (await db.NotificationRoutes.AsNoTracking().SingleAsync(r => r.NotificationKey == key)).Revision;
        var result = await h.Config.UpdateRouteAsync(key, new(kind, true, true, null, null), revision, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Ok, result.Status, result.Message);
    }

    private static async Task EnableSendingAsync(NotificationLifecycleHarness h)
    {
        await using var db = h.NewContext();
        var s = await db.NotificationSettings.AsNoTracking().SingleAsync();
        var result = await h.Config.UpdateSettingsAsync(
            new(true, false, s.FailureDelayMinutes, s.OverdueGraceMinutes, s.ReminderIntervalHours, s.CoverageWarnHours, s.CoverageWarnPercent,
                s.CoverageRecoverHours, s.CoverageRecoverPercent, s.CoverageGapMinutes, s.RetentionDays), s.Revision, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Ok, result.Status, result.Message);
    }

    [TestMethod]
    public async Task SmtpOnly_WithMatrixAbsent_TestsEveryRecipient_AndDeliversAnOpeningThroughTheRealWorker()
    {
        using var keys = new KeyScope();
        await using var smtpServer = new FakeSmtpServer();
        await using var h = await NotificationLifecycleHarness.CreateAsync(
            realAdapters: true, providerOverrides: [SmtpTrusting(smtpServer)]);
        await h.SeedSourceAsync();

        await ConfigureSmtpAsync(h, smtpServer);
        Assert.HasCount(2, smtpServer.Messages, "The test message goes to every configured recipient.");
        Assert.IsTrue(smtpServer.Messages.All(m => m.Data.Contains("M3Undle test notification")));
        smtpServer.Messages.Clear();

        await RouteAsync(h, NotificationKeys.EpgFetchFailed, NotificationProviderKinds.Smtp);
        await EnableSendingAsync(h);

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();
        while (await h.ProcessAsync(NotificationProviderKinds.Smtp)) { }

        Assert.HasCount(2, smtpServer.Messages, "One independent delivery per recipient.");
        CollectionAssert.AreEquivalent(new[] { "a@example.org", "b@example.org" }, smtpServer.Messages.SelectMany(m => m.Recipients).ToArray());
        var data = smtpServer.Messages[0].Data;
        StringAssert.Contains(data, "Guide source");
        StringAssert.Contains(data, "failing to update");
        Assert.DoesNotContain("p@ss", data);
        Assert.IsTrue((await h.DeliveriesAsync()).All(d => d.Delivery.State == NotificationDeliveryStates.Accepted));

        await using var db = h.NewContext();
        var matrix = await db.NotificationDestinations.AsNoTracking().SingleAsync(d => d.Kind == NotificationProviderKinds.Matrix);
        Assert.IsFalse(matrix.Enabled, "Matrix stayed unconfigured and disabled the whole time.");
    }

    [TestMethod]
    public async Task MatrixOnly_DiscoversTheDevice_VerifiesThatRevision_AndDeliversThroughTheHomeserver()
    {
        using var keys = new KeyScope();
        await using var homeserver = await FakeMatrixHomeserver.StartAsync();
        await using var h = await NotificationLifecycleHarness.CreateAsync(
            realAdapters: true, providerOverrides: [MatrixProvider()], allowInsecureMatrixHttp: true);
        await h.SeedSourceAsync();

        await ConfigureMatrixAsync(h, homeserver);
        await using (var db = h.NewContext())
        {
            var matrix = await db.NotificationMatrixSettings.AsNoTracking().SingleAsync();
            Assert.AreEqual("DEV1", matrix.DeviceId, "The device is resolved and stored by the test.");
            Assert.AreEqual("@bot:test", matrix.BotUserId);
            Assert.AreNotEqual(FakeMatrixHomeserver.Token, matrix.AccessTokenEncrypted, "The token is stored encrypted.");
        }

        Assert.AreEqual(1, homeserver.DistinctEvents, "The real test message is a Matrix notice in the room.");
        await RouteAsync(h, NotificationKeys.EpgFetchFailed, NotificationProviderKinds.Matrix);
        await EnableSendingAsync(h);

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "timeout");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();
        while (await h.ProcessAsync(NotificationProviderKinds.Matrix)) { }

        Assert.AreEqual(2, homeserver.DistinctEvents);
        var delivery = (await h.DeliveriesAsync()).Single().Delivery;
        Assert.AreEqual(NotificationDeliveryStates.Accepted, delivery.State);
        Assert.AreEqual("$event2", delivery.RemoteReference);
    }

    [TestMethod]
    public async Task AMatrixOutage_RetriesOnItsOwn_WithoutDelayingSmtpDelivery()
    {
        using var keys = new KeyScope();
        await using var smtpServer = new FakeSmtpServer();
        var homeserver = await FakeMatrixHomeserver.StartAsync();
        await using var h = await NotificationLifecycleHarness.CreateAsync(
            realAdapters: true, providerOverrides: [SmtpTrusting(smtpServer), MatrixProvider()], allowInsecureMatrixHttp: true);
        await h.SeedSourceAsync();
        await ConfigureSmtpAsync(h, smtpServer);
        await ConfigureMatrixAsync(h, homeserver);
        smtpServer.Messages.Clear();

        await RouteAsync(h, NotificationKeys.EpgFetchFailed, NotificationProviderKinds.Smtp);
        await RouteAsync(h, NotificationKeys.EpgRefreshOverdue, NotificationProviderKinds.Matrix);
        await EnableSendingAsync(h);
        await homeserver.DisposeAsync();

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();

        // Give the Matrix-routed condition an incident too, so both transports have queued work.
        await using (var db = h.NewContext())
        {
            var source = await db.EpgSources.SingleAsync();
            source.LastCheckedUtc = h.Time.UtcNow.AddHours(-30);
            await db.SaveChangesAsync();
        }

        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.RunAsync();

        while (await h.ProcessAsync(NotificationProviderKinds.Smtp)) { }
        await h.ProcessAsync(NotificationProviderKinds.Matrix);

        var all = await h.DeliveriesAsync();
        Assert.IsTrue(all.Where(d => d.Delivery.ProviderKind == NotificationProviderKinds.Smtp).All(d => d.Delivery.State == NotificationDeliveryStates.Accepted),
            "SMTP delivery is unaffected by the dead homeserver.");
        var matrixDelivery = all.Single(d => d.Delivery.ProviderKind == NotificationProviderKinds.Matrix).Delivery;
        Assert.AreEqual(NotificationDeliveryStates.RetryScheduled, matrixDelivery.State);
        Assert.AreEqual("network", matrixDelivery.ErrorCode);
        Assert.IsGreaterThan(h.Time.UtcNow, matrixDelivery.DueUtc, "The outage backs off instead of looping.");
    }
}
