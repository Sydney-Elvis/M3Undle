using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Application;

public sealed class HdHomeRunTunerCountResolver(
    IOptions<HdHomeRunOptions> options,
    IServiceScopeFactory scopeFactory)
{
    public int ResolveTunerCount()
    {
        var dbOverride = QueryDbTunerCountOverride();
        if (dbOverride is > 0)
            return Math.Clamp(dbOverride.Value, 1, 32);

        var providerLimit = QueryActiveProviderStreamLimit();
        if (providerLimit is > 0)
            return Math.Clamp(providerLimit.Value, 1, 32);

        return Math.Clamp(options.Value.TunerCount, 1, 32);
    }

    public int? ResolveStreamLimit()
    {
        var dbOverride = QueryDbTunerCountOverride();
        if (dbOverride is > 0)
            return Math.Clamp(dbOverride.Value, 1, 32);

        var providerLimit = QueryActiveProviderStreamLimit();
        if (providerLimit is > 0)
            return Math.Clamp(providerLimit.Value, 1, 32);

        return null;
    }

    internal int? QueryActiveProviderStreamLimit()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.Providers
            .AsNoTracking()
            .Where(x => x.IsActive && x.Enabled)
            .Select(x => x.MaxConcurrentStreams)
            .FirstOrDefault();
    }

    private int? QueryDbTunerCountOverride()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.SiteSettings
            .AsNoTracking()
            .Select(x => x.HdhrTunerCountOverride)
            .FirstOrDefault();
    }
}
