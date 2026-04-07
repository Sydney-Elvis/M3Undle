using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public sealed record RefreshScheduleSettings(
    string ScheduleKind,
    bool StartupCatchup)
{
    public int? IntervalHours => ScheduleKind switch
    {
        "1h" => 1,
        "2h" => 2,
        "4h" => 4,
        "6h" => 6,
        "12h" => 12,
        "24h" => 24,
        _ => null, // "manual" → null
    };

    public bool IsManual => ScheduleKind == "manual";
}

public interface IRefreshScheduleService
{
    Task<RefreshScheduleSettings> GetSettingsAsync(CancellationToken ct = default);
    Task<(bool Succeeded, string? Error)> UpdateAsync(RefreshScheduleSettings settings, CancellationToken ct = default);
    Task<DateTime?> GetNextScheduledRefreshUtcAsync(CancellationToken ct = default);
}

public sealed class RefreshScheduleService(
    ApplicationDbContext db,
    AppEventBus eventBus) : IRefreshScheduleService
{
    private static readonly string[] ValidKinds = ["manual", "1h", "2h", "4h", "6h", "12h", "24h"];

    public async Task<RefreshScheduleSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        var s = await db.SiteSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(ct)
            ?? new SiteSettings { Id = 1 };

        return Map(s);
    }

    public async Task<(bool Succeeded, string? Error)> UpdateAsync(RefreshScheduleSettings settings, CancellationToken ct = default)
    {
        if (!ValidKinds.Contains(settings.ScheduleKind))
            return (false, $"Invalid schedule kind '{settings.ScheduleKind}'. Valid values: {string.Join(", ", ValidKinds)}.");

        var row = await db.SiteSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new SiteSettings { Id = 1 };
            db.SiteSettings.Add(row);
        }

        row.RefreshScheduleKind = settings.ScheduleKind;
        row.RefreshStartupCatchup = settings.StartupCatchup;
        await db.SaveChangesAsync(ct);

        eventBus.Publish(AppEventKind.RefreshScheduleChanged);
        return (true, null);
    }

    public async Task<DateTime?> GetNextScheduledRefreshUtcAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (settings.IsManual || settings.IntervalHours is null)
            return null;

        var lastSnapshot = await db.Snapshots
            .AsNoTracking()
            .Where(s => s.Status == "active")
            .OrderByDescending(s => s.CreatedUtc)
            .Select(s => (DateTime?)s.CreatedUtc)
            .FirstOrDefaultAsync(ct);

        var baseline = lastSnapshot ?? DateTime.UtcNow;
        return baseline.AddHours(settings.IntervalHours.Value);
    }

    private static RefreshScheduleSettings Map(SiteSettings s) =>
        new(s.RefreshScheduleKind, s.RefreshStartupCatchup);
}
