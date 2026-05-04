using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Observability;

public interface IObservabilitySettingsService
{
    Task<ObservabilitySettingsSnapshot> GetSettingsAsync(CancellationToken ct = default);
    Task<ObservabilitySettingsUpdateResult> UpdateAsync(UpdateObservabilitySettingsCommand command, CancellationToken ct = default);
}

public sealed class ObservabilitySettingsService(
    ApplicationDbContext db,
    IMemoryCache cache,
    IOptions<ObservabilityOptions> options) : IObservabilitySettingsService
{
    private const string CacheKey = "m3undle:observability:settings";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    public async Task<ObservabilitySettingsSnapshot> GetSettingsAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out ObservabilitySettingsSnapshot? cached) && cached is not null)
            return cached;

        var settings = await EnsureSiteSettingsAsync(ct);
        var snapshot = Map(settings, options.Value.Metrics);
        cache.Set(CacheKey, snapshot, CacheDuration);
        return snapshot;
    }

    public async Task<ObservabilitySettingsUpdateResult> UpdateAsync(UpdateObservabilitySettingsCommand command, CancellationToken ct = default)
    {
        var settings = await EnsureSiteSettingsAsync(ct);
        var mode = MetricsAccessModes.Normalize(command.Mode);
        var cidrs = NormalizeCidrs(command.LocalAllowedCidrs).ToArray();

        if (!MetricsAccessModes.IsValid(command.Mode))
            return new(false, "Metrics mode must be Disabled, LocalOnly, Token, or Public.", Map(settings, options.Value.Metrics));

        foreach (var cidr in cidrs)
        {
            if (!ParsedIpNetwork.TryParse(cidr, out _))
                return new(false, $"'{cidr}' is not a valid CIDR network.", Map(settings, options.Value.Metrics));
        }

        settings.ObservabilityMetricsEnabled = command.Enabled;
        settings.ObservabilityMetricsMode = mode;
        settings.ObservabilityMetricsEnableChannelLabels = command.EnableChannelLabels;
        settings.ObservabilityMetricsLocalAllowedCidrs = string.Join('\n', cidrs);

        await db.SaveChangesAsync(ct);
        var snapshot = Map(settings, options.Value.Metrics);
        cache.Set(CacheKey, snapshot, CacheDuration);
        return new(true, null, snapshot);
    }

    private async Task<SiteSettings> EnsureSiteSettingsAsync(CancellationToken ct)
    {
        var site = await db.SiteSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (site is not null)
            return site;

        site = new SiteSettings { Id = 1 };
        db.SiteSettings.Add(site);
        await db.SaveChangesAsync(ct);
        return site;
    }

    private static ObservabilitySettingsSnapshot Map(SiteSettings settings, MetricsEndpointOptions options)
    {
        var enabled = options.Enabled && settings.ObservabilityMetricsEnabled;
        var mode = enabled
            ? MetricsAccessModes.Normalize(settings.ObservabilityMetricsMode)
            : MetricsAccessModes.Disabled;

        return new ObservabilitySettingsSnapshot(
            Enabled: enabled,
            Path: options.NormalizedPath,
            Mode: mode,
            EnableChannelLabels: settings.ObservabilityMetricsEnableChannelLabels || options.EnableChannelLabels,
            LocalAllowedCidrs: NormalizeCidrs(settings.ObservabilityMetricsLocalAllowedCidrs)
                .Concat(options.LocalAllowedCidrs ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static IEnumerable<string> NormalizeCidrs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        foreach (var part in value.Split(['\r', '\n', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return part;
    }

    private static IEnumerable<string> NormalizeCidrs(IEnumerable<string>? values)
    {
        if (values is null)
            yield break;

        foreach (var value in values)
        {
            foreach (var cidr in NormalizeCidrs(value))
                yield return cidr;
        }
    }
}

public sealed record ObservabilitySettingsSnapshot(
    bool Enabled,
    string Path,
    string Mode,
    bool EnableChannelLabels,
    IReadOnlyList<string> LocalAllowedCidrs);

public sealed record UpdateObservabilitySettingsCommand(
    bool Enabled,
    string Mode,
    bool EnableChannelLabels,
    IReadOnlyList<string> LocalAllowedCidrs);

public sealed record ObservabilitySettingsUpdateResult(
    bool Succeeded,
    string? Error,
    ObservabilitySettingsSnapshot Settings);
