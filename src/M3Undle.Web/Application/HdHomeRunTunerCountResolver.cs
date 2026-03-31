using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Application;

public sealed class HdHomeRunTunerCountResolver(
    IOptions<HdHomeRunOptions> options,
    IServiceScopeFactory scopeFactory)
{
    private readonly Lock _cacheLock = new();
    private DateTimeOffset _cacheLastUpdated;
    private int _cachedTunerCount;
    private int? _cachedStreamLimit;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);

    public int ResolveTunerCount()
    {
        EnsureCache();
        return _cachedTunerCount;
    }

    public int? ResolveStreamLimit()
    {
        EnsureCache();
        return _cachedStreamLimit;
    }

    private void EnsureCache()
    {
        var now = DateTimeOffset.UtcNow;
        if (_cacheLastUpdated != default && now - _cacheLastUpdated < CacheDuration)
            return;

        lock (_cacheLock)
        {
            now = DateTimeOffset.UtcNow;
            if (_cacheLastUpdated != default && now - _cacheLastUpdated < CacheDuration)
                return;

            var dbOverride = QueryDbTunerCountOverride();
            if (dbOverride is > 0)
            {
                var clamped = Math.Clamp(dbOverride.Value, 1, 32);
                _cachedTunerCount = clamped;
                _cachedStreamLimit = clamped;
            }
            else
            {
                var providerLimit = QueryActiveProviderStreamLimit();
                if (providerLimit is > 0)
                {
                    var clamped = Math.Clamp(providerLimit.Value, 1, 32);
                    _cachedTunerCount = clamped;
                    _cachedStreamLimit = clamped;
                }
                else
                {
                    _cachedTunerCount = Math.Clamp(options.Value.TunerCount, 1, 32);
                    _cachedStreamLimit = null;
                }
            }

            _cacheLastUpdated = now;
        }
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
