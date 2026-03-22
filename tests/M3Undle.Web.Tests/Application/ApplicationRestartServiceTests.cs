using M3Undle.Web.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;

namespace M3Undle.Web.Tests.Application;

/// <summary>
/// Checklist: Restart M3Undle stops the app process cleanly.
/// Tests the ApplicationRestartService mechanism: RequestRestartAsync returns
/// immediately (fire-and-forget) and then calls IHostApplicationLifetime.StopApplication()
/// after a brief grace delay so the HTTP response can complete first.
///
/// Note: verifying the full process exits with code 0 and no orphan threads requires the
/// integration harness (Python run_tests.py) running against a live app instance.
/// </summary>
[TestClass]
public sealed class ApplicationRestartServiceTests
{
    [TestMethod]
    public async Task RequestRestartAsync_ReturnsImmediately_BeforeStopApplicationFires()
    {
        var lifetime = new FakeHostApplicationLifetime();
        var service = new ApplicationRestartService(lifetime);

        var sw = Stopwatch.StartNew();
        await service.RequestRestartAsync();
        sw.Stop();

        // Must return well before the 1-second grace delay fires
        Assert.IsTrue(sw.ElapsedMilliseconds < 500,
            $"RequestRestartAsync should return immediately but took {sw.ElapsedMilliseconds}ms");

        // StopApplication has not been called yet
        Assert.AreEqual(0, lifetime.StopApplicationCallCount);
    }

    [TestMethod]
    public async Task RequestRestartAsync_CallsStopApplicationAfterGraceDelay()
    {
        var lifetime = new FakeHostApplicationLifetime();
        var service = new ApplicationRestartService(lifetime);

        await service.RequestRestartAsync();

        // Wait long enough for the 1-second internal delay to elapse
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, lifetime.StopApplicationCallCount,
            "StopApplication should have been called exactly once after the grace delay.");
    }

    [TestMethod]
    public async Task RequestRestartAsync_CalledTwice_StopsApplicationTwice()
    {
        // Each call fires an independent fire-and-forget — both eventually call StopApplication.
        var lifetime = new FakeHostApplicationLifetime();
        var service = new ApplicationRestartService(lifetime);

        await service.RequestRestartAsync();
        await service.RequestRestartAsync();

        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.AreEqual(2, lifetime.StopApplicationCallCount);
    }

    // -------------------------------------------------------------------------
    // Fake IHostApplicationLifetime
    // -------------------------------------------------------------------------

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        private int _stopCount;

        public int StopApplicationCallCount => Volatile.Read(ref _stopCount);

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Interlocked.Increment(ref _stopCount);
    }
}
