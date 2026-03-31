using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public interface IHdHomeRunSettingsService
{
    Task<HdhrSettingsState> GetSettingsAsync(CancellationToken ct = default);
    Task<HdhrSettingsUpdateResult> UpdateAsync(UpdateHdhrSettingsCommand command, CancellationToken ct = default);
    Task ClearRestartRequiredAsync(CancellationToken ct = default);
}

public sealed class HdHomeRunSettingsService(
    ApplicationDbContext db,
    HdHomeRunDeviceService deviceService,
    HdHomeRunTunerCountResolver tunerCountResolver) : IHdHomeRunSettingsService
{
    public async Task<HdhrSettingsState> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(ct)
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
        var settings = await db.SiteSettings.FirstOrDefaultAsync(ct);
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
        var settings = await db.SiteSettings.FirstOrDefaultAsync(ct);
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
            AdvertisedBaseUrl: settings.HdhrAdvertisedBaseUrl,
            ResolvedBaseUrl: deviceService.ResolveBaseUrl(),
            DiscoveryEnabled: settings.HdhrDiscoveryEnabled,
            SsdpEnabled: settings.HdhrSsdpEnabled,
            SiliconDustDiscoveryEnabled: settings.HdhrSiliconDustDiscoveryEnabled);
    }

    private HdhrSettingsSnapshot BuildAppliedSnapshot(int? providerLimit)
    {
        var effectiveTunerCount = tunerCountResolver.ResolveTunerCount();
        var isLimitEnforced = tunerCountResolver.ResolveStreamLimit() is not null;

        return new HdhrSettingsSnapshot(
            Enabled: deviceService.IsEnabled,
            EffectiveTunerCount: effectiveTunerCount,
            TunerCountOverride: null,
            ProviderTunerLimit: providerLimit,
            IsStreamLimitEnforced: isLimitEnforced,
            AdvertisedBaseUrl: null,
            ResolvedBaseUrl: deviceService.ResolveBaseUrl(),
            DiscoveryEnabled: deviceService.IsDiscoveryEnabled,
            SsdpEnabled: deviceService.IsSsdpEnabled,
            SiliconDustDiscoveryEnabled: deviceService.IsSiliconDustDiscoveryEnabled);
    }

    private static IEnumerable<string> Validate(UpdateHdhrSettingsCommand command)
    {
        if (command.TunerCountOverride is < 1 or > 32)
            yield return "Tuner count override must be between 1 and 32.";
        if (command.AdvertisedBaseUrl is { Length: > 0 } url &&
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            yield return "Advertised base URL must be a valid absolute URL.";
        else if (command.AdvertisedBaseUrl is { Length: > 0 } url2 &&
            Uri.TryCreate(url2, UriKind.Absolute, out var uri2) &&
            uri2.Scheme is not "http" and not "https")
            yield return "Advertised base URL must use http or https.";
    }
}

public sealed record HdhrSettingsSnapshot(
    bool Enabled,
    int EffectiveTunerCount,
    int? TunerCountOverride,
    int? ProviderTunerLimit,
    bool IsStreamLimitEnforced,
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
