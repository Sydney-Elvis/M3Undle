using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using M3Undle.Web.Data;

namespace M3Undle.Web.Application;

public sealed class AdaptiveLockoutOptions
{
    public int InitialFailedAttempts { get; set; } = 5;
    public TimeSpan InitialLockoutTimeSpan { get; set; } = TimeSpan.FromSeconds(30);
    public int EscalatedFailedAttempts { get; set; } = 3;
    public TimeSpan EscalatedLockoutTimeSpan { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed record AdaptiveLockoutResult(
    bool LockoutApplied,
    TimeSpan? LockoutDuration,
    int FailedAttemptCount,
    bool Escalated);

public interface IAdaptiveLockoutService
{
    Task<AdaptiveLockoutResult> RegisterFailedPasswordAttemptAsync(ApplicationUser user, CancellationToken cancellationToken);
    Task ResetAsync(ApplicationUser user, CancellationToken cancellationToken);
}

public sealed class AdaptiveLockoutService(
    ApplicationDbContext db,
    TimeProvider timeProvider,
    IOptions<AdaptiveLockoutOptions> options) : IAdaptiveLockoutService
{
    private readonly AdaptiveLockoutOptions _options = options.Value;

    public async Task<AdaptiveLockoutResult> RegisterFailedPasswordAttemptAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Atomic increment prevents lost-update under concurrent brute-force requests.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE AspNetUsers SET AccessFailedCount = AccessFailedCount + 1 WHERE Id = {user.Id}",
            cancellationToken);

        await db.Entry(user).ReloadAsync(cancellationToken);
        var newCount = user.AccessFailedCount;

        var shouldApplyEscalatedLockout = user.AdaptiveLockoutEscalated
            && newCount >= _options.EscalatedFailedAttempts;

        var shouldApplyInitialLockout = !user.AdaptiveLockoutEscalated
            && newCount >= _options.InitialFailedAttempts;

        if (shouldApplyEscalatedLockout)
        {
            user.LockoutEnd = now.Add(_options.EscalatedLockoutTimeSpan);
            user.AccessFailedCount = 0;
        }
        else if (shouldApplyInitialLockout)
        {
            user.LockoutEnd = now.Add(_options.InitialLockoutTimeSpan);
            user.AccessFailedCount = 0;
            user.AdaptiveLockoutEscalated = true;
        }

        if (shouldApplyEscalatedLockout || shouldApplyInitialLockout)
        {
            db.Update(user);
            await db.SaveChangesAsync(cancellationToken);
        }

        return new AdaptiveLockoutResult(
            LockoutApplied: shouldApplyInitialLockout || shouldApplyEscalatedLockout,
            LockoutDuration: shouldApplyEscalatedLockout
                ? _options.EscalatedLockoutTimeSpan
                : shouldApplyInitialLockout
                    ? _options.InitialLockoutTimeSpan
                    : null,
            FailedAttemptCount: newCount,
            Escalated: user.AdaptiveLockoutEscalated);
    }

    public async Task ResetAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        if (user.AccessFailedCount == 0 && !user.AdaptiveLockoutEscalated && user.LockoutEnd is null)
            return;

        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.AdaptiveLockoutEscalated = false;

        db.Update(user);
        await db.SaveChangesAsync(cancellationToken);
    }
}
