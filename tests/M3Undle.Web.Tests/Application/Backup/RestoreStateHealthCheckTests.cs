using M3Undle.Web.Application;
using M3Undle.Web.Application.Backup;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application.Backup;

[TestClass]
public sealed class RestoreStateHealthCheckTests
{
    [TestMethod]
    public async Task CheckHealthAsync_ReflectsRestoreOutcomes()
    {
        var tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-restore-health-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDataDir);
        try
        {
            var stateStore = new RestoreStateStore(new RuntimePaths(tempDataDir, string.Empty, string.Empty, string.Empty, string.Empty));
            var healthCheck = new RestoreStateHealthCheck(stateStore);
            var context = new HealthCheckContext();

            Assert.AreEqual(HealthStatus.Healthy, (await healthCheck.CheckHealthAsync(context)).Status, "No marker means healthy.");

            stateStore.Write(Marker(RestoreState.Requested));
            Assert.AreEqual(HealthStatus.Healthy, (await healthCheck.CheckHealthAsync(context)).Status, "A staged restore is not a failure.");

            stateStore.Write(Marker(RestoreState.Completed));
            Assert.AreEqual(HealthStatus.Healthy, (await healthCheck.CheckHealthAsync(context)).Status);

            stateStore.Write(Marker(RestoreState.Failed, "boom"));
            var failed = await healthCheck.CheckHealthAsync(context);
            Assert.AreEqual(HealthStatus.Degraded, failed.Status);
            Assert.IsTrue(failed.Description!.Contains("boom", StringComparison.Ordinal));

            stateStore.Write(Marker(RestoreState.RolledBack, "swap failed"));
            var rolledBack = await healthCheck.CheckHealthAsync(context);
            Assert.AreEqual(HealthStatus.Degraded, rolledBack.Status);
            Assert.IsTrue(rolledBack.Description!.Contains("rolled back", StringComparison.OrdinalIgnoreCase));

            stateStore.Clear();
            Assert.AreEqual(HealthStatus.Healthy, (await healthCheck.CheckHealthAsync(context)).Status, "Dismissing the status clears the degraded state.");
        }
        finally
        {
            Directory.Delete(tempDataDir, recursive: true);
        }
    }

    private static RestoreStateMarker Marker(RestoreState state, string? error = null) => new()
    {
        State = state,
        BackupId = "b1",
        ArchiveFileName = "backup.m3undle-backup",
        UpdatedUtc = DateTime.UtcNow,
        ErrorMessage = error,
    };
}
