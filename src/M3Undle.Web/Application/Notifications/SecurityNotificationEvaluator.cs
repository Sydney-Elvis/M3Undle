using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

/// <summary>
/// Turns failed sign-in observations into one counted summary per account reference per fixed five-minute window, once the
/// window has closed. A failure storm therefore produces a handful of summaries, not one message per attempt, and the summary
/// carries only a count and a keyed account reference.
/// </summary>
public sealed class SecurityNotificationEvaluator(
    ApplicationDbContext db,
    NotificationOccurrenceWriter writer,
    TimeProvider timeProvider)
{
    private const int MaxObservationsPerPass = 5_000;

    public async Task SummariseClosedWindowsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var closedBefore = NotificationSecurityRecorder.WindowStart(now);

        var rows = await db.NotificationConditionObservations
            .Where(x => !x.Consumed && x.EvidenceKey == NotificationEvidenceKeys.LoginAttempt && x.CompletedUtc < closedBefore)
            .OrderBy(x => x.ObservationId)
            .Take(MaxObservationsPerPass)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return;

        foreach (var group in rows.GroupBy(r => (r.SubjectId, Window: NotificationSecurityRecorder.WindowStart(r.CompletedUtc))))
        {
            var count = group.Count();
            var reference = NotificationSecurityRecorder.Reference(group.Key.SubjectId);
            var end = group.Key.Window + NotificationSecurityRecorder.LoginWindow;
            await writer.StageOneTimeAsync(new OneTimeNotification(
                NotificationKeys.SecurityLoginFailed,
                $"{NotificationKeys.SecurityLoginFailed}:{group.Key.SubjectId}:{new DateTimeOffset(group.Key.Window, TimeSpan.Zero).ToUnixTimeSeconds()}",
                "Warning",
                count == 1 ? "A sign-in attempt failed" : $"{count} sign-in attempts failed",
                $"{count} failed sign-in attempt{(count == 1 ? string.Empty : "s")} for the account with reference {reference} between " +
                $"{group.Key.Window:u} and {end:u}. The name that was typed, the client address and the browser are not included.",
                $"Account {reference}", null, group.Key.Window), cancellationToken);

            foreach (var row in group)
            {
                row.Consumed = true;
                row.ConsumedUtc = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
