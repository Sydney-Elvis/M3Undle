using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Backup;

public sealed record BackupScheduleSettings(bool Enabled, DateTime? LastRunUtc);

public interface IBackupScheduleService
{
    Task<BackupScheduleSettings> GetSettingsAsync(CancellationToken ct = default);
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);
    Task RecordRunAsync(DateTime runUtc, CancellationToken ct = default);

    /// <summary>Null if disabled. A fixed weekly cadence — see plan §6, no cron/day-of-week configuration.</summary>
    Task<DateTime?> GetNextScheduledBackupUtcAsync(CancellationToken ct = default);
}

public sealed class BackupScheduleService(ApplicationDbContext db) : IBackupScheduleService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    public async Task<BackupScheduleSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = await db.SiteSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(ct)
            ?? new SiteSettings { Id = 1 };

        return new BackupScheduleSettings(settings.BackupScheduleEnabled, settings.BackupLastRunUtc);
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        var row = await GetOrCreateRowAsync(ct);
        row.BackupScheduleEnabled = enabled;
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordRunAsync(DateTime runUtc, CancellationToken ct = default)
    {
        var row = await GetOrCreateRowAsync(ct);
        row.BackupLastRunUtc = runUtc;
        await db.SaveChangesAsync(ct);
    }

    public async Task<DateTime?> GetNextScheduledBackupUtcAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (!settings.Enabled)
            return null;

        var baseline = settings.LastRunUtc ?? DateTime.UtcNow;
        var next = baseline + Interval;

        // If the schedule was off for a while, don't return a time already in the past — roll
        // forward by whole intervals, same idiom as RefreshScheduleService.
        var now = DateTime.UtcNow;
        if (next < now)
        {
            var behind = now.Ticks - next.Ticks;
            next = next.AddTicks(((behind / Interval.Ticks) + 1) * Interval.Ticks);
        }

        return next;
    }

    private async Task<SiteSettings> GetOrCreateRowAsync(CancellationToken ct)
    {
        var row = await db.SiteSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (row is not null)
            return row;

        row = new SiteSettings { Id = 1 };
        db.SiteSettings.Add(row);
        return row;
    }
}
