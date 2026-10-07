using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

[TestClass]
public sealed class OperationalNotificationLifecycleTests
{
    private static async Task AddProviderAsync(NotificationLifecycleHarness h, string id, string name, bool enabled = true)
    {
        await using var db = h.NewContext();
        db.Providers.Add(new Provider { ProviderId = id, Name = name, Enabled = enabled, PlaylistUrl = "http://x/p.m3u", CreatedUtc = h.Time.UtcNow, UpdatedUtc = h.Time.UtcNow });
        await db.SaveChangesAsync();
    }

    private static async Task AddIntegrationAsync(NotificationLifecycleHarness h, string id, string name, bool enabled = true)
    {
        await using var db = h.NewContext();
        db.DownstreamIntegrations.Add(new DownstreamIntegration
        {
            DownstreamIntegrationId = id, Name = name, Kind = "webhook", BaseUrl = "http://x/hook", Enabled = enabled,
            CreatedUtc = h.Time.UtcNow, UpdatedUtc = h.Time.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- provider fetch

    [TestMethod]
    public async Task ProviderFetchFailure_IsAnnouncedAfterTheDelay_AndRecoversOnlyForThatProvider()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await AddProviderAsync(h, "p1", "Alpha");
        await AddProviderAsync(h, "p2", "Beta");
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.ProviderFetchFailed);

        await h.StageRawObservationAsync(NotificationEvidenceKeys.ProviderFetch, NotificationSubjectKinds.Provider, "p1", NotificationObservationOutcomes.Failed, "http");
        await h.StageRawObservationAsync(NotificationEvidenceKeys.ProviderFetch, NotificationSubjectKinds.Provider, "p2", NotificationObservationOutcomes.Failed, "timeout");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();

        var openings = await h.DeliveriesAsync();
        Assert.HasCount(2, openings);
        StringAssert.Contains(openings.Single(o => o.Occurrence.SubjectLabel == "Alpha").Occurrence.Body, "http");
        foreach (var o in openings)
            await h.AcceptAsync(o.Delivery.DeliveryId);

        await h.StageRawObservationAsync(NotificationEvidenceKeys.ProviderFetch, NotificationSubjectKinds.Provider, "p1", NotificationObservationOutcomes.Ok);
        await h.RunAsync();

        var incidents = await h.IncidentsAsync(NotificationKeys.ProviderFetchFailed);
        Assert.AreEqual(NotificationIncidentStates.Resolved, incidents.Single(i => i.SubjectId == "p1").State);
        Assert.AreEqual(NotificationIncidentStates.Active, incidents.Single(i => i.SubjectId == "p2").State, "One provider's recovery never clears another's.");
        var recoveries = (await h.DeliveriesAsync()).Where(d => d.Occurrence.Kind == NotificationOccurrenceKinds.Recovery).ToList();
        Assert.HasCount(1, recoveries);
        StringAssert.Contains(recoveries[0].Occurrence.Title, "Alpha");
    }

    [TestMethod]
    public async Task ProviderFetchFailure_DoesNotDependOnUiEvents_AndRemovedProvidersCloseWithoutRecovery()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await AddProviderAsync(h, "p1", "Alpha");
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.ProviderFetchFailed);

        await h.StageRawObservationAsync(NotificationEvidenceKeys.ProviderFetch, NotificationSubjectKinds.Provider, "p1", NotificationObservationOutcomes.Failed, "http");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();
        await h.AcceptAsync((await h.DeliveriesAsync()).Single().Delivery.DeliveryId);

        await using (var db = h.NewContext())
        {
            // No UI event ever existed, and the provider is now gone.
            db.Providers.RemoveRange(await db.Providers.ToListAsync());
            await db.SaveChangesAsync();
        }

        await h.RunAsync();
        Assert.AreEqual(NotificationIncidentStates.Closed, (await h.IncidentsAsync()).Single().State);
        Assert.IsFalse((await h.DeliveriesAsync()).Any(d => d.Occurrence.Kind == NotificationOccurrenceKinds.Recovery));
    }

    [TestMethod]
    public void ProviderFailureClasses_NeverCarryProviderText()
    {
        Assert.AreEqual("parse", M3Undle.Web.Application.SnapshotBuilder.ClassifyProviderFailure(new M3Undle.Web.Application.ProviderParseException("secret url http://u:p@h")));
        Assert.AreEqual("http", M3Undle.Web.Application.SnapshotBuilder.ClassifyProviderFailure(new HttpRequestException("secret")));
        Assert.AreEqual("timeout", M3Undle.Web.Application.SnapshotBuilder.ClassifyProviderFailure(new TaskCanceledException("secret")));
        Assert.AreEqual("http", M3Undle.Web.Application.SnapshotBuilder.ClassifyProviderFailure(new M3Undle.Web.Application.ProviderFetchException("m", new HttpRequestException("x"))));
        Assert.AreEqual("provider", M3Undle.Web.Application.SnapshotBuilder.ClassifyProviderFailure(new M3Undle.Web.Application.ProviderFetchException("secret")));
        Assert.AreEqual("error", M3Undle.Web.Application.SnapshotBuilder.ClassifyProviderFailure(new InvalidOperationException("secret")));
    }

    // ---------------------------------------------------------------- downstream

    [TestMethod]
    public async Task DownstreamFailure_UsesCommittedOutcomes_AndASuccessfulLaterCommandRecovers()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await AddIntegrationAsync(h, "i1", "Jellyfin");
        await AddIntegrationAsync(h, "i2", "Emby");
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.DownstreamRefreshFailed);

        await h.StageRawObservationAsync(NotificationEvidenceKeys.DownstreamCommand, NotificationSubjectKinds.DownstreamIntegration, "i1", NotificationObservationOutcomes.Failed, "auth");
        await h.StageRawObservationAsync(NotificationEvidenceKeys.DownstreamCommand, NotificationSubjectKinds.DownstreamIntegration, "i2", NotificationObservationOutcomes.Ok);
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();

        var opening = (await h.DeliveriesAsync()).Single();
        StringAssert.Contains(opening.Occurrence.Title, "Jellyfin");
        StringAssert.Contains(opening.Occurrence.Body, "auth");
        await h.AcceptAsync(opening.Delivery.DeliveryId);

        await h.StageRawObservationAsync(NotificationEvidenceKeys.DownstreamCommand, NotificationSubjectKinds.DownstreamIntegration, "i1", NotificationObservationOutcomes.Ok);
        await h.RunAsync();
        Assert.AreEqual(NotificationIncidentStates.Resolved, (await h.IncidentsAsync()).Single().State);
        Assert.AreEqual(1, (await h.DeliveriesAsync()).Count(d => d.Occurrence.Kind == NotificationOccurrenceKinds.Recovery));
    }

    [TestMethod]
    public void DownstreamFailureClasses_AreCoarse()
    {
        Assert.AreEqual("credential", M3Undle.Web.Application.Downstream.DownstreamNotificationService.ClassifyFailure("Failed to decrypt API key: whatever"));
        Assert.AreEqual("timeout", M3Undle.Web.Application.Downstream.DownstreamNotificationService.ClassifyFailure("The request timed out"));
        Assert.AreEqual("auth", M3Undle.Web.Application.Downstream.DownstreamNotificationService.ClassifyFailure("HTTP 401 for http://secret/hook"));
        Assert.AreEqual("network", M3Undle.Web.Application.Downstream.DownstreamNotificationService.ClassifyFailure("Connection refused by host"));
        Assert.AreEqual("error", M3Undle.Web.Application.Downstream.DownstreamNotificationService.ClassifyFailure("something odd with token=abc"));
    }

    // ---------------------------------------------------------------- stream instability

    private static async Task AddFailuresAsync(NotificationLifecycleHarness h, string provider, string channel, string name, params TimeSpan[] agesAgo)
    {
        await using var db = h.NewContext();
        foreach (var age in agesAgo)
        {
            db.StreamChannelHealthEvents.Add(new StreamChannelHealthEvent
            {
                StreamChannelHealthEventId = Guid.NewGuid().ToString("N"), ProviderId = provider, ProviderChannelId = channel,
                DisplayName = name, EventKind = "UpstreamFailure", EventUtc = h.Time.UtcNow - age,
            });
        }

        await db.SaveChangesAsync();
    }

    private static ActiveChannelStream Live(NotificationLifecycleHarness h, string provider, string channel, TimeSpan? sinceRecovery = null) =>
        new(provider, channel, IsLive: true, h.Time.GetUtcNow(), sinceRecovery is { } s ? h.Time.GetUtcNow() - s : null);

    [TestMethod]
    public async Task StreamInstability_NeedsThreeFailuresInFiveMinutes_AndTwoSustainedMinutes()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await AddProviderAsync(h, "p1", "Alpha");
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.StreamUnstable);

        await AddFailuresAsync(h, "p1", "c1", "CNN", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        await h.RunAsync();
        Assert.IsEmpty(await h.IncidentsAsync(), "Two failures are not instability.");

        await AddFailuresAsync(h, "p1", "c1", "CNN", TimeSpan.Zero);
        await h.RunAsync();
        Assert.HasCount(1, await h.IncidentsAsync(NotificationKeys.StreamUnstable));
        Assert.IsEmpty(await h.DeliveriesAsync(), "The condition is not yet sustained for two minutes.");

        h.Time.Advance(TimeSpan.FromSeconds(125));
        await h.RunAsync();
        var delivery = (await h.DeliveriesAsync()).Single();
        StringAssert.Contains(delivery.Occurrence.Title, "CNN");
        StringAssert.Contains(delivery.Occurrence.Title, "Alpha", "The provider context is shown.");
        Assert.DoesNotContain("http", delivery.Occurrence.Body.Replace("https://", ""), "No upstream URLs.");
    }

    [TestMethod]
    public async Task OldFailuresOutsideTheWindow_DoNotCountTowardsInstability()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await AddProviderAsync(h, "p1", "Alpha");
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.StreamUnstable);

        await AddFailuresAsync(h, "p1", "c1", "CNN", TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30));
        await h.RunAsync();
        Assert.IsEmpty(await h.IncidentsAsync());
    }

    [TestMethod]
    public async Task StreamRecovery_RequiresTwoMinutesOfHealthyOutputOnThatChannel_AndOtherChannelsCannotClearIt()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await AddProviderAsync(h, "p1", "Alpha");
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.StreamUnstable);

        // A burst, then it keeps failing so the condition is genuinely sustained past two minutes.
        await AddFailuresAsync(h, "p1", "c1", "CNN", TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), TimeSpan.Zero);
        await h.RunAsync();
        h.Time.Advance(TimeSpan.FromSeconds(110));
        await AddFailuresAsync(h, "p1", "c1", "CNN", TimeSpan.Zero);
        h.Time.Advance(TimeSpan.FromSeconds(15));
        await h.RunAsync();
        await h.AcceptAsync((await h.DeliveriesAsync()).Single().Delivery.DeliveryId);

        // Another channel streaming healthily proves nothing about this one.
        h.ActiveStreams.Sessions.Add(Live(h, "p1", "other-channel"));
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.RunAsync();
        Assert.AreEqual(NotificationIncidentStates.Active, (await h.IncidentsAsync()).Single().State);

        // This channel is live again, but its last failure was only 45 seconds ago: not yet healthy.
        h.ActiveStreams.Sessions.Clear();
        h.ActiveStreams.Sessions.Add(Live(h, "p1", "c1"));
        await h.RunAsync();
        Assert.AreEqual(NotificationIncidentStates.Active, (await h.IncidentsAsync()).Single().State);

        h.Time.Advance(TimeSpan.FromSeconds(90));
        h.ActiveStreams.Sessions.Clear();
        h.ActiveStreams.Sessions.Add(Live(h, "p1", "c1"));
        await h.RunAsync();
        Assert.AreEqual(NotificationIncidentStates.Resolved, (await h.IncidentsAsync()).Single().State, "Two minutes of healthy output resolves it.");
        Assert.AreEqual(1, (await h.DeliveriesAsync()).Count(d => d.Occurrence.Kind == NotificationOccurrenceKinds.Recovery));
    }

    [TestMethod]
    public async Task ASessionThatEnds_ClosesMonitoringWithoutASuccessNotice_AndStaleOutputIsNotHealthy()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await AddProviderAsync(h, "p1", "Alpha");
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.StreamUnstable);

        await AddFailuresAsync(h, "p1", "c1", "CNN", TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), TimeSpan.Zero);
        await h.RunAsync();
        h.Time.Advance(TimeSpan.FromSeconds(110));
        await AddFailuresAsync(h, "p1", "c1", "CNN", TimeSpan.Zero);
        h.Time.Advance(TimeSpan.FromSeconds(15));
        await h.RunAsync();
        await h.AcceptAsync((await h.DeliveriesAsync()).Single().Delivery.DeliveryId);

        // Minutes later the session exists but its last byte is old, so it is not healthy output.
        h.Time.Advance(TimeSpan.FromMinutes(5));
        h.ActiveStreams.Sessions.Add(new ActiveChannelStream("p1", "c1", true, h.Time.GetUtcNow().AddMinutes(-3), null));
        await h.RunAsync();
        Assert.AreEqual(NotificationIncidentStates.Active, (await h.IncidentsAsync()).Single().State);

        // The viewer leaves: after the grace period monitoring ends. That is not a recovery.
        h.ActiveStreams.Sessions.Clear();
        await h.RunAsync();
        h.Time.Advance(TimeSpan.FromSeconds(130));
        await h.RunAsync();
        Assert.AreEqual(NotificationIncidentStates.Closed, (await h.IncidentsAsync()).Single().State);
        Assert.IsFalse((await h.DeliveriesAsync()).Any(d => d.Occurrence.Kind == NotificationOccurrenceKinds.Recovery));
    }

    [TestMethod]
    public async Task IncidentsSurviveARestart_BecauseTheyAreBuiltFromPersistedHealthEvents()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await AddProviderAsync(h, "p1", "Alpha");
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.StreamUnstable);
        await AddFailuresAsync(h, "p1", "c1", "CNN", TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20));

        // A "new process" has no session state, only the database.
        h.State.RelevanceStamp = null;
        await h.RunAsync();
        Assert.HasCount(1, await h.IncidentsAsync(NotificationKeys.StreamUnstable));
    }
}
