using M3Undle.Web.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Epg;

/// <summary>
/// Unit tests for SnapshotBuilder.IsEpgCacheFresh — the per-source cadence gate that
/// decides whether a network EPG fetch can be skipped in favour of the on-disk cache.
///
/// The predicate is pure (no DB, no HTTP, no filesystem access), so every case is
/// expressed as a simple call with a controlled clock value.
/// </summary>
[TestClass]
public sealed class EpgCadenceTests
{
    private static readonly DateTimeOffset ReferenceNow =
        new(2026, 4, 12, 10, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------
    // No prior successful fetch — cache is never fresh
    // -------------------------------------------------------------------------

    [TestMethod]
    public void NoLastSuccess_CacheIsNotFresh()
    {
        var fresh = SnapshotBuilder.IsEpgCacheFresh(
            lastSuccessUtc: null,
            intervalHours: 24,
            utcNow: ReferenceNow);

        Assert.IsFalse(fresh, "Without a prior successful fetch the cache cannot be considered fresh.");
    }

    // -------------------------------------------------------------------------
    // Within the interval — cache is fresh, skip network fetch
    // -------------------------------------------------------------------------

    [TestMethod]
    public void LastSuccessWithinInterval_CacheIsFresh()
    {
        // Last success 12 h ago, interval 24 h → still within window.
        var lastSuccess = ReferenceNow.UtcDateTime.AddHours(-12);

        var fresh = SnapshotBuilder.IsEpgCacheFresh(
            lastSuccessUtc: lastSuccess,
            intervalHours: 24,
            utcNow: ReferenceNow);

        Assert.IsTrue(fresh, "A last-success time within the interval should be treated as fresh.");
    }

    [TestMethod]
    public void LastSuccessOneSecondBeforeInterval_CacheIsFresh()
    {
        // 1 s before expiry → still fresh.
        var lastSuccess = ReferenceNow.UtcDateTime.AddHours(-6).AddSeconds(1);

        var fresh = SnapshotBuilder.IsEpgCacheFresh(
            lastSuccessUtc: lastSuccess,
            intervalHours: 6,
            utcNow: ReferenceNow);

        Assert.IsTrue(fresh);
    }

    // -------------------------------------------------------------------------
    // At or beyond the interval — cache is stale, fetch is required
    // -------------------------------------------------------------------------

    [TestMethod]
    public void LastSuccessExactlyAtInterval_CacheIsNotFresh()
    {
        // Elapsed == intervalHours → not fresh (< is the condition, so == is stale).
        var lastSuccess = ReferenceNow.UtcDateTime.AddHours(-6);

        var fresh = SnapshotBuilder.IsEpgCacheFresh(
            lastSuccessUtc: lastSuccess,
            intervalHours: 6,
            utcNow: ReferenceNow);

        Assert.IsFalse(fresh, "A last-success time exactly at the interval boundary should not be treated as fresh.");
    }

    [TestMethod]
    public void LastSuccessBeyondInterval_CacheIsNotFresh()
    {
        // 25 h since last success, interval 24 h → stale.
        var lastSuccess = ReferenceNow.UtcDateTime.AddHours(-25);

        var fresh = SnapshotBuilder.IsEpgCacheFresh(
            lastSuccessUtc: lastSuccess,
            intervalHours: 24,
            utcNow: ReferenceNow);

        Assert.IsFalse(fresh);
    }

    // -------------------------------------------------------------------------
    // Per-source override vs global fallback semantics
    // -------------------------------------------------------------------------

    /// <summary>
    /// A per-source interval of 48 h keeps the cache fresh when the global schedule
    /// would have expired it (e.g. global = 6 h, source override = 48 h).
    /// </summary>
    [TestMethod]
    public void PerSourceOverride_LongerThanGlobal_CacheRemainesFresh()
    {
        // Last success 12 h ago; source overrides to 48 h → still fresh.
        var lastSuccess = ReferenceNow.UtcDateTime.AddHours(-12);

        var fresh = SnapshotBuilder.IsEpgCacheFresh(
            lastSuccessUtc: lastSuccess,
            intervalHours: 48,     // per-source override
            utcNow: ReferenceNow);

        Assert.IsTrue(fresh,
            "A per-source 48 h override must keep the cache fresh when only 12 h have elapsed.");
    }

    /// <summary>
    /// A per-source interval shorter than the global schedule expires the cache sooner.
    /// </summary>
    [TestMethod]
    public void PerSourceOverride_ShorterThanGlobal_CacheExpiresEarly()
    {
        // Last success 7 h ago; source overrides to 6 h → stale even if global is 24 h.
        var lastSuccess = ReferenceNow.UtcDateTime.AddHours(-7);

        var fresh = SnapshotBuilder.IsEpgCacheFresh(
            lastSuccessUtc: lastSuccess,
            intervalHours: 6,     // per-source override
            utcNow: ReferenceNow);

        Assert.IsFalse(fresh,
            "A per-source 6 h override must expire the cache after 7 h even if the global interval is longer.");
    }

    // -------------------------------------------------------------------------
    // Supported interval values (6 | 12 | 24 | 48 | 168)
    // -------------------------------------------------------------------------

    [TestMethod]
    [DataRow(6)]
    [DataRow(12)]
    [DataRow(24)]
    [DataRow(48)]
    [DataRow(168)]
    public void SupportedIntervals_FreshJustInsideWindow(int intervalHours)
    {
        var lastSuccess = ReferenceNow.UtcDateTime.AddHours(-intervalHours).AddMinutes(1);

        var fresh = SnapshotBuilder.IsEpgCacheFresh(
            lastSuccessUtc: lastSuccess,
            intervalHours: intervalHours,
            utcNow: ReferenceNow);

        Assert.IsTrue(fresh, $"Should be fresh with 1 min remaining in a {intervalHours}h window.");
    }

    [TestMethod]
    [DataRow(6)]
    [DataRow(12)]
    [DataRow(24)]
    [DataRow(48)]
    [DataRow(168)]
    public void SupportedIntervals_StaleJustBeyondWindow(int intervalHours)
    {
        var lastSuccess = ReferenceNow.UtcDateTime.AddHours(-intervalHours).AddMinutes(-1);

        var fresh = SnapshotBuilder.IsEpgCacheFresh(
            lastSuccessUtc: lastSuccess,
            intervalHours: intervalHours,
            utcNow: ReferenceNow);

        Assert.IsFalse(fresh, $"Should be stale with 1 min past a {intervalHours}h window.");
    }
}
