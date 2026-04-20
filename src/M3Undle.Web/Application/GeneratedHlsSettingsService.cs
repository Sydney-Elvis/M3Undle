using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.GeneratedHls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Application;

public interface IGeneratedHlsSettingsService
{
    Task<GeneratedHlsSettingsState> GetSettingsAsync(CancellationToken ct = default);
    Task<GeneratedHlsSettingsUpdateResult> UpdateAsync(UpdateGeneratedHlsSettingsCommand command, CancellationToken ct = default);
    Task ClearRestartRequiredAsync(CancellationToken ct = default);
}

public sealed class GeneratedHlsSettingsService(
    ApplicationDbContext db,
    GeneratedHlsSessionManager sessionManager) : IGeneratedHlsSettingsService
{
    public async Task<GeneratedHlsSettingsState> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = await db.SiteSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(ct)
            ?? new SiteSettings { Id = 1 };

        return new GeneratedHlsSettingsState(
            Saved: MapSnapshot(settings),
            FfmpegAvailable: sessionManager.FfmpegAvailable,
            FfmpegUnavailableReason: sessionManager.FfmpegUnavailableReason,
            EffectivelyEnabled: sessionManager.IsEffectivelyEnabled,
            ConfiguredFfmpegPath: sessionManager.ConfiguredFfmpegPath,
            RestartRequired: settings.GeneratedHlsSettingsRestartRequired);
    }

    public async Task<GeneratedHlsSettingsUpdateResult> UpdateAsync(UpdateGeneratedHlsSettingsCommand command, CancellationToken ct = default)
    {
        var settings = await db.SiteSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new SiteSettings { Id = 1 };
            db.SiteSettings.Add(settings);
        }

        var errors = Validate(command).ToArray();
        if (errors.Length > 0)
        {
            return new GeneratedHlsSettingsUpdateResult(
                Succeeded: false,
                Error: string.Join(" ", errors),
                Settings: MapSnapshot(settings),
                RestartRequired: settings.GeneratedHlsSettingsRestartRequired,
                Changed: false);
        }

        var normalizedPath = string.IsNullOrWhiteSpace(command.FfmpegPath) ? null : command.FfmpegPath.Trim();

        var changed =
            settings.GeneratedHlsEnabled != command.Enabled ||
            settings.GeneratedHlsFfmpegPath != normalizedPath;

        settings.GeneratedHlsEnabled = command.Enabled;
        settings.GeneratedHlsFfmpegPath = normalizedPath;

        if (changed)
            settings.GeneratedHlsSettingsRestartRequired = true;

        await db.SaveChangesAsync(ct);

        return new GeneratedHlsSettingsUpdateResult(
            Succeeded: true,
            Error: null,
            Settings: MapSnapshot(settings),
            RestartRequired: settings.GeneratedHlsSettingsRestartRequired,
            Changed: changed);
    }

    public async Task ClearRestartRequiredAsync(CancellationToken ct = default)
    {
        var settings = await db.SiteSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (settings is null || !settings.GeneratedHlsSettingsRestartRequired)
            return;

        settings.GeneratedHlsSettingsRestartRequired = false;
        await db.SaveChangesAsync(ct);
    }

    private static GeneratedHlsSettingsSnapshot MapSnapshot(SiteSettings settings)
        => new(
            Enabled: settings.GeneratedHlsEnabled,
            FfmpegPath: settings.GeneratedHlsFfmpegPath);

    private static IEnumerable<string> Validate(UpdateGeneratedHlsSettingsCommand command)
    {
        if (command.FfmpegPath is { Length: > 0 } path)
        {
            if (path.IndexOfAny(['<', '>', '|', '\0']) >= 0)
                yield return "FFmpeg path contains invalid characters.";
        }
    }
}

public sealed record GeneratedHlsSettingsSnapshot(
    bool Enabled,
    string? FfmpegPath);

public sealed record GeneratedHlsSettingsState(
    GeneratedHlsSettingsSnapshot Saved,
    bool FfmpegAvailable,
    string? FfmpegUnavailableReason,
    bool EffectivelyEnabled,
    string ConfiguredFfmpegPath,
    bool RestartRequired);

public sealed record UpdateGeneratedHlsSettingsCommand(
    bool Enabled,
    string? FfmpegPath);

public sealed record GeneratedHlsSettingsUpdateResult(
    bool Succeeded,
    string? Error,
    GeneratedHlsSettingsSnapshot Settings,
    bool RestartRequired,
    bool Changed);

public sealed class GeneratedHlsDbOptionsConfigurator(IServiceScopeFactory scopeFactory) : IConfigureOptions<GeneratedHlsOptions>
{
    public void Configure(GeneratedHlsOptions options)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = db.SiteSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefault();
        if (settings is null)
            return;

        options.Enabled = settings.GeneratedHlsEnabled;
        if (!string.IsNullOrWhiteSpace(settings.GeneratedHlsFfmpegPath))
            options.FfmpegPath = settings.GeneratedHlsFfmpegPath;
    }
}
