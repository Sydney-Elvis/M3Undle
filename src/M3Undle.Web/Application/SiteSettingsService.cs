using M3Undle.Web.Data;

namespace M3Undle.Web.Application;

public interface ISiteSettingsService
{
    event Action? OnSettingsChanged;
    ValueTask<bool> GetAuthenticationEnabledAsync();
    Task SetAuthenticationEnabledAsync(bool enabled);
}

public sealed class SiteSettingsService : ISiteSettingsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private volatile bool _authEnabled = false;
    private volatile bool _initialized = false;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public event Action? OnSettingsChanged;

    public SiteSettingsService(IServiceScopeFactory scopeFactory)
        => _scopeFactory = scopeFactory;

    public async ValueTask<bool> GetAuthenticationEnabledAsync()
    {
        if (_initialized) return _authEnabled;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return _authEnabled;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = await db.SiteSettings.FindAsync(1);
            _authEnabled = settings?.AuthenticationEnabled ?? false;
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }

        return _authEnabled;
    }

    public async Task SetAuthenticationEnabledAsync(bool enabled)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = await db.SiteSettings.FindAsync(1);
        settings!.AuthenticationEnabled = enabled;
        await db.SaveChangesAsync();
        _authEnabled = enabled;
        _initialized = true;
        OnSettingsChanged?.Invoke();
    }
}

