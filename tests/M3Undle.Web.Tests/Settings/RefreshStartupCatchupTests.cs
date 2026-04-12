using M3Undle.Web.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Settings;

/// <summary>
/// Unit tests for the startup catch-up staleness predicate in SnapshotRefreshService.
///
/// The predicate is deterministic given (lastSnapshotUtc, thresholdHours, utcNow), so
/// these tests run without any database, HTTP, or service-hosting infrastructure.
/// </summary>
[TestClass]
public sealed class RefreshStartupCatchupTests
{
    private static readonly DateTimeOffset ReferenceNow =
        new(2026, 4, 12, 10, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------
    // No prior snapshot — always stale
    // -------------------------------------------------------------------------

    [TestMethod]
    public void NoSnapshot_AlwaysTriggersStartupRefresh()
    {
        var needed = SnapshotRefreshService.IsStartupCatchupNeeded(
            lastSnapshotUtc: null,
            thresholdHours: 6,
            utcNow: ReferenceNow);

        Assert.IsTrue(needed, "With no prior snapshot a startup refresh must always be triggered.");
    }

    // -------------------------------------------------------------------------
    // Snapshot exactly at the threshold boundary
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Snapshot_ExactlyAtThreshold_TriggersRefresh()
    {
        // Snapshot created exactly 6 h ago → elapsed == threshold → stale.
        var lastSnapshot = ReferenceNow.UtcDateTime.AddHours(-6);

        var needed = SnapshotRefreshService.IsStartupCatchupNeeded(
            lastSnapshotUtc: lastSnapshot,
            thresholdHours: 6,
            utcNow: ReferenceNow);

        Assert.IsTrue(needed, "A snapshot that is exactly at the threshold should be treated as stale.");
    }

    [TestMethod]
    public void Snapshot_OneSecondBeyondThreshold_TriggersRefresh()
    {
        var lastSnapshot = ReferenceNow.UtcDateTime.AddHours(-6).AddSeconds(-1);

        var needed = SnapshotRefreshService.IsStartupCatchupNeeded(
            lastSnapshotUtc: lastSnapshot,
            thresholdHours: 6,
            utcNow: ReferenceNow);

        Assert.IsTrue(needed);
    }

    // -------------------------------------------------------------------------
    // Snapshot within the threshold — no refresh needed
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Snapshot_JustInsideThreshold_DoesNotTriggerRefresh()
    {
        // Snapshot created 5 h 59 m ago — still within the 6 h threshold.
        var lastSnapshot = ReferenceNow.UtcDateTime.AddHours(-5).AddMinutes(-59);

        var needed = SnapshotRefreshService.IsStartupCatchupNeeded(
            lastSnapshotUtc: lastSnapshot,
            thresholdHours: 6,
            utcNow: ReferenceNow);

        Assert.IsFalse(needed, "A snapshot within the threshold must not trigger a startup refresh.");
    }

    [TestMethod]
    public void Snapshot_VeryRecent_DoesNotTriggerRefresh()
    {
        // Snapshot just 1 minute old — well within any threshold.
        var lastSnapshot = ReferenceNow.UtcDateTime.AddMinutes(-1);

        var needed = SnapshotRefreshService.IsStartupCatchupNeeded(
            lastSnapshotUtc: lastSnapshot,
            thresholdHours: 6,
            utcNow: ReferenceNow);

        Assert.IsFalse(needed);
    }

    // -------------------------------------------------------------------------
    // Manual schedule uses 24 h fallback threshold
    // -------------------------------------------------------------------------

    [TestMethod]
    public void ManualSchedule_SnapshotOlderThan24h_TriggersRefresh()
    {
        // The caller passes thresholdHours = 24 when the schedule is manual.
        var lastSnapshot = ReferenceNow.UtcDateTime.AddHours(-25);

        var needed = SnapshotRefreshService.IsStartupCatchupNeeded(
            lastSnapshotUtc: lastSnapshot,
            thresholdHours: 24,
            utcNow: ReferenceNow);

        Assert.IsTrue(needed, "Manual schedule with a >24 h old snapshot must trigger a startup refresh.");
    }

    [TestMethod]
    public void ManualSchedule_SnapshotWithin24h_DoesNotTriggerRefresh()
    {
        var lastSnapshot = ReferenceNow.UtcDateTime.AddHours(-12);

        var needed = SnapshotRefreshService.IsStartupCatchupNeeded(
            lastSnapshotUtc: lastSnapshot,
            thresholdHours: 24,
            utcNow: ReferenceNow);

        Assert.IsFalse(needed);
    }

    // -------------------------------------------------------------------------
    // Various configured intervals
    // -------------------------------------------------------------------------

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    [DataRow(6)]
    [DataRow(12)]
    [DataRow(24)]
    public void AllScheduledIntervals_StaleSnapshotTriggersRefresh(int intervalHours)
    {
        // Snapshot is one hour older than the interval.
        var lastSnapshot = ReferenceNow.UtcDateTime.AddHours(-(intervalHours + 1));

        var needed = SnapshotRefreshService.IsStartupCatchupNeeded(
            lastSnapshotUtc: lastSnapshot,
            thresholdHours: intervalHours,
            utcNow: ReferenceNow);

        Assert.IsTrue(needed,
            $"Stale snapshot (interval={intervalHours}h) must trigger startup refresh.");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    [DataRow(6)]
    [DataRow(12)]
    [DataRow(24)]
    public void AllScheduledIntervals_FreshSnapshotDoesNotTriggerRefresh(int intervalHours)
    {
        // Snapshot is one minute newer than the interval.
        var lastSnapshot = ReferenceNow.UtcDateTime.AddHours(-intervalHours).AddMinutes(1);

        var needed = SnapshotRefreshService.IsStartupCatchupNeeded(
            lastSnapshotUtc: lastSnapshot,
            thresholdHours: intervalHours,
            utcNow: ReferenceNow);

        Assert.IsFalse(needed,
            $"Fresh snapshot (interval={intervalHours}h) must not trigger startup refresh.");
    }
}
