using System.Security.Cryptography;
using System.Text;
using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

/// <summary>
/// Captures sign-in outcomes for administrator notification. What was typed is never stored or sent: attempts are grouped
/// by a keyed hash of the identifier, and neither the identifier, the client address nor the user agent leaves this class.
/// Capture follows the policy in force at the moment of the outcome, so nothing is retained while the rows are Off.
/// </summary>
public sealed class NotificationSecurityRecorder(
    ApplicationDbContext db,
    NotificationOccurrenceWriter writer,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(5);

    public async Task RecordFailedLoginAsync(string? identifier, CancellationToken cancellationToken)
    {
        if (!await writer.CanCaptureAsync(NotificationKeys.SecurityLoginFailed, cancellationToken))
            return;

        var hash = await HashAsync(identifier, cancellationToken);
        writer.StageObservation(
            NotificationEvidenceKeys.LoginAttempt, NotificationSubjectKinds.Account, hash, NotificationObservationOutcomes.Failed,
            timeProvider.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Called once per lockout transition. Attempts made while already locked never reach here.</summary>
    public async Task RecordAccountLockedAsync(string? identifier, DateTime lockoutEndUtc, CancellationToken cancellationToken)
    {
        if (!await writer.CanCaptureAsync(NotificationKeys.SecurityAccountLocked, cancellationToken))
            return;

        var hash = await HashAsync(identifier, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await writer.StageOneTimeAsync(new OneTimeNotification(
            NotificationKeys.SecurityAccountLocked,
            $"{NotificationKeys.SecurityAccountLocked}:{hash}:{new DateTimeOffset(lockoutEndUtc, TimeSpan.Zero).ToUnixTimeSeconds()}",
            "Warning",
            "An account was locked after failed sign-ins",
            $"The account with reference {Reference(hash)} was locked after repeated failed sign-ins. It unlocks automatically around {lockoutEndUtc:u}. " +
            "The name that was typed is not included.",
            $"Account {Reference(hash)}", null, now), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public static string Reference(string accountHash) => accountHash[..Math.Min(8, accountHash.Length)];

    public static DateTime WindowStart(DateTime utc)
    {
        var seconds = new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();
        var window = (long)LoginWindow.TotalSeconds;
        return DateTimeOffset.FromUnixTimeSeconds(seconds / window * window).UtcDateTime;
    }

    private async Task<string> HashAsync(string? identifier, CancellationToken cancellationToken)
    {
        var salt = await db.NotificationSettings.AsNoTracking().Select(s => s.IdentifierSalt).FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(salt))
        {
            // Settings seeded before the salt existed get one lazily; the guard keeps concurrent callers on the same value.
            var fresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await db.NotificationSettings.Where(s => s.IdentifierSalt == string.Empty)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.IdentifierSalt, fresh), cancellationToken);
            salt = await db.NotificationSettings.AsNoTracking().Select(s => s.IdentifierSalt).FirstAsync(cancellationToken);
        }

        var normalized = (identifier ?? string.Empty).Trim().ToLowerInvariant();
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(salt), Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
