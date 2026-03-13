namespace M3Undle.Web.Application;

public interface ISiteSettingsService
{
    ValueTask<bool> GetAuthenticationEnabledAsync();
}

public sealed class SiteSettingsService(EnvironmentVariableService env) : ISiteSettingsService
{
    public ValueTask<bool> GetAuthenticationEnabledAsync()
    {
        var value = env.GetValue("M3UNDLE_AUTH_ENABLED");
        return ValueTask.FromResult(string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase));
    }
}
