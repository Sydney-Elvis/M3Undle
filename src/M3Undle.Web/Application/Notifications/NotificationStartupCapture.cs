using M3Undle.Web.Data;

namespace M3Undle.Web.Application.Notifications;

/// <summary>
/// Captures "application started" and "migrations applied" once per boot. It runs after migrations, seeding and any staged
/// restore and before the workers start, so a restored instance (which requires activation) never announces a startup that
/// belongs to the restored timeline.
/// </summary>
public static class NotificationStartupCapture
{
    public static async Task CaptureAsync(
        ApplicationDbContext db,
        NotificationOccurrenceWriter writer,
        string version,
        IReadOnlyCollection<string> appliedMigrations,
        string bootId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await writer.StageOneTimeAsync(new OneTimeNotification(
            NotificationKeys.SystemRestarted,
            $"{NotificationKeys.SystemRestarted}:{bootId}",
            "Info", "M3Undle started", $"M3Undle v{version} started.", null, null, nowUtc), cancellationToken);

        if (appliedMigrations.Count > 0)
        {
            var names = appliedMigrations.Select(m => m[(m.IndexOf('_') + 1)..]);
            await writer.StageOneTimeAsync(new OneTimeNotification(
                NotificationKeys.SystemMigrationsApplied,
                $"{NotificationKeys.SystemMigrationsApplied}:{bootId}",
                "Info", "Database migrations applied",
                $"{appliedMigrations.Count} database migration(s) were applied during startup: {string.Join(", ", names)}.",
                null, null, nowUtc), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
