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

public sealed record EffectiveRefreshScheduleSettings(
    string? ProfileId,
    RefreshScheduleSettings Settings,
    bool UsesProfileOverride,
    RefreshScheduleSettings GlobalSettings);

public sealed record ProfileRefreshScheduleSettings(
    bool InheritGlobal,
    string ScheduleKind,
    bool StartupCatchup);

public interface IRefreshScheduleService
{
    Task<RefreshScheduleSettings> GetSettingsAsync(CancellationToken ct = default);
    Task<(bool Succeeded, string? Error)> UpdateAsync(RefreshScheduleSettings settings, CancellationToken ct = default);
    Task<DateTime?> GetNextScheduledRefreshUtcAsync(CancellationToken ct = default);
    Task<EffectiveRefreshScheduleSettings> GetEffectiveSettingsAsync(string profileId, CancellationToken ct = default);
    Task<EffectiveRefreshScheduleSettings?> GetActiveProfileSettingsAsync(CancellationToken ct = default);
    Task<(bool Succeeded, string? Error)> UpdateProfileAsync(
        string profileId,
        ProfileRefreshScheduleSettings settings,
        CancellationToken ct = default);
    Task<DateTime?> GetNextScheduledRefreshUtcAsync(string profileId, CancellationToken ct = default);
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
        var intervalHours = settings.IntervalHours.Value;
        var next = baseline.AddHours(intervalHours);

        var now = DateTime.UtcNow;
        if (next < now)
        {
            var intervalTicks = TimeSpan.FromHours(intervalHours).Ticks;
            var behind = now.Ticks - next.Ticks;
            next = next.AddTicks(((behind / intervalTicks) + 1) * intervalTicks);
        }

        return next;
    }

    public async Task<EffectiveRefreshScheduleSettings> GetEffectiveSettingsAsync(string profileId, CancellationToken ct = default)
    {
        var global = await GetSettingsAsync(ct);
        var profile = await db.Profiles
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId)
            .Select(x => new
            {
                x.ProfileId,
                x.RefreshScheduleKindOverride,
                x.RefreshStartupCatchupOverride,
            })
            .SingleOrDefaultAsync(ct);

        if (profile is null)
            return new EffectiveRefreshScheduleSettings(profileId, global, UsesProfileOverride: false, global);

        var usesOverride = !string.IsNullOrWhiteSpace(profile.RefreshScheduleKindOverride);
        var effective = usesOverride
            ? new RefreshScheduleSettings(
                profile.RefreshScheduleKindOverride!,
                profile.RefreshStartupCatchupOverride ?? global.StartupCatchup)
            : global;

        return new EffectiveRefreshScheduleSettings(profile.ProfileId, effective, usesOverride, global);
    }

    public async Task<EffectiveRefreshScheduleSettings?> GetActiveProfileSettingsAsync(CancellationToken ct = default)
    {
        var profileId = await db.Profiles
            .AsNoTracking()
            .Where(x => x.IsActive && x.Enabled)
            .Select(x => x.ProfileId)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(profileId))
            return null;

        return await GetEffectiveSettingsAsync(profileId, ct);
    }

    public async Task<(bool Succeeded, string? Error)> UpdateProfileAsync(
        string profileId,
        ProfileRefreshScheduleSettings settings,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return (false, "Profile id is required.");

        if (!settings.InheritGlobal && !ValidKinds.Contains(settings.ScheduleKind))
            return (false, $"Invalid schedule kind '{settings.ScheduleKind}'. Valid values: {string.Join(", ", ValidKinds)}.");

        var profile = await db.Profiles.SingleOrDefaultAsync(x => x.ProfileId == profileId, ct);
        if (profile is null)
            return (false, "Profile not found.");

        if (settings.InheritGlobal)
        {
            profile.RefreshScheduleKindOverride = null;
            profile.RefreshStartupCatchupOverride = null;
        }
        else
        {
            profile.RefreshScheduleKindOverride = settings.ScheduleKind;
            profile.RefreshStartupCatchupOverride = settings.StartupCatchup;
        }

        profile.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        eventBus.Publish(AppEventKind.RefreshScheduleChanged);
        return (true, null);
    }

    public async Task<DateTime?> GetNextScheduledRefreshUtcAsync(string profileId, CancellationToken ct = default)
    {
        var effective = await GetEffectiveSettingsAsync(profileId, ct);
        return await GetNextScheduledRefreshUtcAsync(effective.Settings, profileId, ct);
    }

    private static RefreshScheduleSettings Map(SiteSettings s) =>
        new(s.RefreshScheduleKind, s.RefreshStartupCatchup);

    private async Task<DateTime?> GetNextScheduledRefreshUtcAsync(
        RefreshScheduleSettings settings,
        string? profileId,
        CancellationToken ct)
    {
        if (settings.IsManual || settings.IntervalHours is null)
            return null;

        var query = db.Snapshots
            .AsNoTracking()
            .Where(s => s.Status == "active");

        if (!string.IsNullOrWhiteSpace(profileId))
            query = query.Where(s => s.ProfileId == profileId);

        var lastSnapshot = await query
            .OrderByDescending(s => s.CreatedUtc)
            .Select(s => (DateTime?)s.CreatedUtc)
            .FirstOrDefaultAsync(ct);

        var baseline = lastSnapshot ?? DateTime.UtcNow;
        var intervalHours = settings.IntervalHours.Value;
        var next = baseline.AddHours(intervalHours);

        var now = DateTime.UtcNow;
        if (next < now)
        {
            var intervalTicks = TimeSpan.FromHours(intervalHours).Ticks;
            var behind = now.Ticks - next.Ticks;
            next = next.AddTicks(((behind / intervalTicks) + 1) * intervalTicks);
        }

        return next;
    }
}
