using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public interface IStreamingSettingsService
{
    Task<bool> GetEnabledAsync(CancellationToken ct = default);
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);
}

public sealed class StreamingSettingsService(ApplicationDbContext db) : IStreamingSettingsService
{
    public async Task<bool> GetEnabledAsync(CancellationToken ct = default)
    {
        var settings = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return settings?.StreamingEnabled ?? true;
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        var settings = await db.SiteSettings.FirstOrDefaultAsync(ct);
        if (settings is null) return;
        settings.StreamingEnabled = enabled;
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
