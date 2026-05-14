using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace M3Undle.Web.Application;

public interface IHdHomeRunSettingsService
{
    Task<HdhrSettingsState> GetSettingsAsync(CancellationToken ct = default);
    Task<HdhrSettingsUpdateResult> UpdateAsync(UpdateHdhrSettingsCommand command, CancellationToken ct = default);
    Task ClearRestartRequiredAsync(CancellationToken ct = default);
}

public sealed class HdHomeRunSettingsService(
    IServiceScopeFactory scopeFactory,
    HdHomeRunDeviceService deviceService,
    HdHomeRunTunerCountResolver tunerCountResolver) : IHdHomeRunSettingsService
{
    public async Task<HdhrSettingsState> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = await db.SiteSettings
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(ct)
            ?? new SiteSettings { Id = 1 };

        var providerLimit = tunerCountResolver.QueryActiveProviderStreamLimit();
        var saved = MapSnapshot(settings, providerLimit);
        var applied = BuildAppliedSnapshot(providerLimit);

        return new HdhrSettingsState(
            Saved: saved,
            Applied: applied,
            RestartRequired: settings.HdhrSettingsRestartRequired,
            DisabledByEnvironment: deviceService.IsDisabledByEnvironment);
    }

    public async Task<HdhrSettingsUpdateResult> UpdateAsync(UpdateHdhrSettingsCommand command, CancellationToken ct = default)
    {
           await using var scope = scopeFactory.CreateAsyncScope();
           var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
           var settings = await db.SiteSettings
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new SiteSettings { Id = 1 };
            db.SiteSettings.Add(settings);
        }

        var errors = Validate(command).ToArray();
        if (errors.Length > 0)
        {
            var providerLimit = tunerCountResolver.QueryActiveProviderStreamLimit();
            return new HdhrSettingsUpdateResult(
                Succeeded: false,
                Error: string.Join(" ", errors),
                Settings: MapSnapshot(settings, providerLimit),
                RestartRequired: settings.HdhrSettingsRestartRequired,
                Changed: false);
        }

        var changed =
            settings.HdhrEnabled != command.Enabled ||
            settings.HdhrTunerCountOverride != command.TunerCountOverride ||
            settings.HdhrAdvertisedBaseUrl != command.AdvertisedBaseUrl ||
            settings.HdhrDiscoveryEnabled != command.DiscoveryEnabled ||
            settings.HdhrSsdpEnabled != command.SsdpEnabled ||
            settings.HdhrSiliconDustDiscoveryEnabled != command.SiliconDustDiscoveryEnabled;

        settings.HdhrEnabled = command.Enabled;
        settings.HdhrTunerCountOverride = command.TunerCountOverride;
        settings.HdhrAdvertisedBaseUrl = command.AdvertisedBaseUrl;
        settings.HdhrDiscoveryEnabled = command.DiscoveryEnabled;
        settings.HdhrSsdpEnabled = command.SsdpEnabled;
        settings.HdhrSiliconDustDiscoveryEnabled = command.SiliconDustDiscoveryEnabled;
        settings.HdhrFriendlyName = string.IsNullOrWhiteSpace(command.FriendlyName) ? null : command.FriendlyName.Trim();

        if (changed)
            settings.HdhrSettingsRestartRequired = true;

        await db.SaveChangesAsync(ct);

        var limit = tunerCountResolver.QueryActiveProviderStreamLimit();
        return new HdhrSettingsUpdateResult(
            Succeeded: true,
            Error: null,
            Settings: MapSnapshot(settings, limit),
            RestartRequired: settings.HdhrSettingsRestartRequired,
            Changed: changed);
    }

    public async Task ClearRestartRequiredAsync(CancellationToken ct = default)
    {
           await using var scope = scopeFactory.CreateAsyncScope();
           var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
           var settings = await db.SiteSettings
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(ct);
        if (settings is null || !settings.HdhrSettingsRestartRequired)
            return;

        settings.HdhrSettingsRestartRequired = false;
        await db.SaveChangesAsync(ct);
    }

    private HdhrSettingsSnapshot MapSnapshot(SiteSettings settings, int? providerLimit)
    {
        var effectiveTunerCount = tunerCountResolver.ResolveTunerCount();
        var isLimitEnforced = tunerCountResolver.ResolveStreamLimit() is not null;

        return new HdhrSettingsSnapshot(
            Enabled: settings.HdhrEnabled,
            EffectiveTunerCount: effectiveTunerCount,
            TunerCountOverride: settings.HdhrTunerCountOverride,
            ProviderTunerLimit: providerLimit,
            IsStreamLimitEnforced: isLimitEnforced,
            FriendlyName: settings.HdhrFriendlyName,
            ResolvedFriendlyName: deviceService.ResolveFriendlyName(),
            AdvertisedBaseUrl: settings.HdhrAdvertisedBaseUrl,
            ResolvedBaseUrl: deviceService.ResolveBaseUrl(),
            DiscoveryEnabled: settings.HdhrDiscoveryEnabled,
            SsdpEnabled: settings.HdhrSsdpEnabled,
            SiliconDustDiscoveryEnabled: settings.HdhrSiliconDustDiscoveryEnabled);
    }

    private HdhrSettingsSnapshot BuildAppliedSnapshot(int? providerLimit)
    {
        var runtime = deviceService.GetRuntimeSnapshot();
        var effectiveTunerCount = tunerCountResolver.ResolveTunerCount();
        var isLimitEnforced = tunerCountResolver.ResolveStreamLimit() is not null;

        return new HdhrSettingsSnapshot(
            Enabled: runtime.Enabled,
            EffectiveTunerCount: effectiveTunerCount,
            TunerCountOverride: null,
            ProviderTunerLimit: providerLimit,
            IsStreamLimitEnforced: isLimitEnforced,
            FriendlyName: null,
            ResolvedFriendlyName: deviceService.ResolveFriendlyName(),
            AdvertisedBaseUrl: null,
            ResolvedBaseUrl: runtime.ResolvedBaseUrl,
            DiscoveryEnabled: runtime.DiscoveryEnabled,
            SsdpEnabled: runtime.SsdpEnabled,
            SiliconDustDiscoveryEnabled: runtime.SiliconDustDiscoveryEnabled);
    }

    private static IEnumerable<string> Validate(UpdateHdhrSettingsCommand command)
    {
        if (command.TunerCountOverride is < 1 or > 32)
            yield return "Tuner count override must be between 1 and 32.";
        if (command.FriendlyName is { Length: > 0 } name && name.Trim().Length > 128)
            yield return "Friendly name must be 128 characters or fewer.";
        if (command.AdvertisedBaseUrl is { Length: > 0 } url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                yield return "Advertised base URL must be a valid absolute URL.";
            else if (uri.Scheme is not "http" and not "https")
                yield return "Advertised base URL must use http or https.";
            else if (IsLoopbackHost(uri.Host))
                yield return "Advertised base URL must not be a loopback or localhost address.";
        }
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return System.Net.IPAddress.TryParse(host, out var addr) &&
               (System.Net.IPAddress.IsLoopback(addr) ||
                addr.Equals(System.Net.IPAddress.Any) ||
                addr.Equals(System.Net.IPAddress.IPv6Any));
    }
}

public sealed record HdhrSettingsSnapshot(
    bool Enabled,
    int EffectiveTunerCount,
    int? TunerCountOverride,
    int? ProviderTunerLimit,
    bool IsStreamLimitEnforced,
    string? FriendlyName,
    string ResolvedFriendlyName,
    string? AdvertisedBaseUrl,
    string ResolvedBaseUrl,
    bool DiscoveryEnabled,
    bool SsdpEnabled,
    bool SiliconDustDiscoveryEnabled);

public sealed record HdhrSettingsState(
    HdhrSettingsSnapshot Saved,
    HdhrSettingsSnapshot Applied,
    bool RestartRequired,
    bool DisabledByEnvironment);

public sealed record UpdateHdhrSettingsCommand(
    bool Enabled,
    int? TunerCountOverride,
    string? FriendlyName,
    string? AdvertisedBaseUrl,
    bool DiscoveryEnabled,
    bool SsdpEnabled,
    bool SiliconDustDiscoveryEnabled);

public sealed record HdhrSettingsUpdateResult(
    bool Succeeded,
    string? Error,
    HdhrSettingsSnapshot Settings,
    bool RestartRequired,
    bool Changed);
