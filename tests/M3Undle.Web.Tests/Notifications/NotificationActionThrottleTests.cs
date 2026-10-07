using M3Undle.Web.Application.Notifications;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

[TestClass]
public sealed class NotificationActionThrottleTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [TestMethod]
    public void AllowsUpToTheLimitPerBucket_ThenReportsWhenTheOldestHitExpires()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var throttle = new NotificationActionThrottle(time);

        Assert.IsTrue(throttle.TryAcquire("test:smtp", 2, Minute, 100, out _));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.IsTrue(throttle.TryAcquire("test:smtp", 2, Minute, 100, out _));
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.IsFalse(throttle.TryAcquire("test:smtp", 2, Minute, 100, out var retry));
        Assert.AreEqual(TimeSpan.FromSeconds(40), retry, "The oldest hit leaves the window in 40 seconds.");

        Assert.IsTrue(throttle.TryAcquire("test:matrix", 2, Minute, 100, out _), "Buckets are independent.");

        time.Advance(TimeSpan.FromSeconds(41));
        Assert.IsTrue(throttle.TryAcquire("test:smtp", 2, Minute, 100, out _), "The window slides.");
    }

    [TestMethod]
    public void TheServerWideCeilingHoldsEvenWhenEveryCallerLooksDifferent()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var throttle = new NotificationActionThrottle(time);

        for (var i = 0; i < 5; i++)
            Assert.IsTrue(throttle.TryAcquire($"bucket-{i}", 10, Minute, serverWideLimit: 5, out _));

        Assert.IsFalse(throttle.TryAcquire("bucket-new", 10, Minute, serverWideLimit: 5, out var retry), "Anonymous-mode flooding is capped globally.");
        Assert.IsGreaterThan(TimeSpan.Zero, retry);
    }
}
