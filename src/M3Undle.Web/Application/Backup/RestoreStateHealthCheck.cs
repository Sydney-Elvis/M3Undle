using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace M3Undle.Web.Application.Backup;

/// <summary>
/// Surfaces the last restore outcome through the health endpoints so a failed restore —
/// in particular an M3UNDLE_RESTORE_FILE attempt on a headless container (plan §9.1) — is
/// visible to Docker healthchecks and external monitors, not just buried in the startup log.
/// Degraded, not Unhealthy: the app is running (on the prior database after a failure or
/// rollback), it just needs operator attention. Clears when the operator dismisses the status.
/// </summary>
public sealed class RestoreStateHealthCheck(RestoreStateStore stateStore) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var marker = stateStore.Read();
        var result = marker?.State switch
        {
            RestoreState.Failed => HealthCheckResult.Degraded(
                $"Restore of '{marker.ArchiveFileName}' failed: {marker.ErrorMessage}"),
            RestoreState.RolledBack => HealthCheckResult.Degraded(
                $"Restore of '{marker.ArchiveFileName}' failed and was rolled back: {marker.ErrorMessage}"),
            _ => HealthCheckResult.Healthy(),
        };

        return Task.FromResult(result);
    }
}
