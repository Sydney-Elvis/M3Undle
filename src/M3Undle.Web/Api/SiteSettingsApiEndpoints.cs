using M3Undle.Web.Application;
using M3Undle.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace M3Undle.Web.Api;

public static class SiteSettingsApiEndpoints
{
    public static IEndpointRouteBuilder MapSiteSettingsApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/settings");
        group.RequireAuthorization(UiAccessPolicy.Name);
        group.WithTags("Settings");

        group.MapGet("/endpoint-security", GetEndpointSecurityAsync).WithSummary("Get endpoint security settings");
        group.MapPut("/endpoint-security", UpdateEndpointSecurityAsync).WithSummary("Update endpoint security settings");
        group.MapGet("/generated-hls", GetGeneratedHlsSettingsAsync).WithSummary("Get generated HLS settings");
        group.MapPut("/generated-hls", UpdateGeneratedHlsSettingsAsync).WithSummary("Update generated HLS settings");
        group.MapGet("/refresh-schedule", GetRefreshScheduleAsync).WithSummary("Get refresh schedule settings");
        group.MapPut("/refresh-schedule", UpdateRefreshScheduleAsync).WithSummary("Update refresh schedule settings");
        group.MapGet("/hdhr", GetHdhrSettingsAsync).WithSummary("Get HDHomeRun settings");
        group.MapPut("/hdhr", UpdateHdhrSettingsAsync).WithSummary("Update HDHomeRun settings");

        return app;
    }

    private static async Task<Ok<EndpointSecurityResponse>> GetEndpointSecurityAsync(
        IEndpointSecurityService endpointSecurityService,
        CancellationToken cancellationToken)
    {
        var settings = await endpointSecurityService.GetSettingsAsync(cancellationToken);
        return TypedResults.Ok(new EndpointSecurityResponse(
            Enabled: settings.Enabled,
            Username: settings.Username,
            HasCredential: settings.HasCredential,
            ActiveProfileId: settings.ActiveProfileId,
            VirtualTunerId: settings.VirtualTunerId));
    }

    private static async Task<Results<Ok<EndpointSecurityResponse>, ValidationProblem>> UpdateEndpointSecurityAsync(
        EndpointSecurityUpdateRequest request,
        IEndpointSecurityService endpointSecurityService,
        CancellationToken cancellationToken)
    {
        var result = await endpointSecurityService.UpdateAsync(new UpdateEndpointSecurityCommand(
            Enabled: request.Enabled,
            Username: request.Username,
            Password: request.Password,
            ActiveProfileId: request.ActiveProfileId,
            VirtualTunerId: request.VirtualTunerId), cancellationToken);

        if (!result.Succeeded)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["endpointSecurity"] = [result.Error ?? "Endpoint security update failed."],
            });
        }

        return TypedResults.Ok(new EndpointSecurityResponse(
            Enabled: result.Settings.Enabled,
            Username: result.Settings.Username,
            HasCredential: result.Settings.HasCredential,
            ActiveProfileId: result.Settings.ActiveProfileId,
            VirtualTunerId: result.Settings.VirtualTunerId));
    }

    private sealed record EndpointSecurityUpdateRequest(
        bool Enabled,
        string? Username,
        string? Password,
        string? ActiveProfileId,
        string? VirtualTunerId);

    private sealed record EndpointSecurityResponse(
        bool Enabled,
        string? Username,
        bool HasCredential,
        string? ActiveProfileId,
        string? VirtualTunerId);

    private static async Task<Ok<GeneratedHlsSettingsResponse>> GetGeneratedHlsSettingsAsync(
        IGeneratedHlsSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var state = await settingsService.GetSettingsAsync(cancellationToken);
        return TypedResults.Ok(new GeneratedHlsSettingsResponse(
            Enabled: state.Saved.Enabled,
            FfmpegPath: state.Saved.FfmpegPath,
            FfmpegAvailable: state.FfmpegAvailable,
            FfmpegUnavailableReason: state.FfmpegUnavailableReason,
            EffectivelyEnabled: state.EffectivelyEnabled,
            ConfiguredFfmpegPath: state.ConfiguredFfmpegPath,
            RestartRequired: state.RestartRequired));
    }

    private static async Task<Results<Ok<GeneratedHlsSettingsResponse>, ValidationProblem>> UpdateGeneratedHlsSettingsAsync(
        GeneratedHlsSettingsUpdateRequest request,
        IGeneratedHlsSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var result = await settingsService.UpdateAsync(new UpdateGeneratedHlsSettingsCommand(
            Enabled: request.Enabled,
            FfmpegPath: request.FfmpegPath), cancellationToken);

        if (!result.Succeeded)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["generatedHls"] = [result.Error ?? "Generated HLS settings update failed."],
            });
        }

        var state = await settingsService.GetSettingsAsync(cancellationToken);
        return TypedResults.Ok(new GeneratedHlsSettingsResponse(
            Enabled: state.Saved.Enabled,
            FfmpegPath: state.Saved.FfmpegPath,
            FfmpegAvailable: state.FfmpegAvailable,
            FfmpegUnavailableReason: state.FfmpegUnavailableReason,
            EffectivelyEnabled: state.EffectivelyEnabled,
            ConfiguredFfmpegPath: state.ConfiguredFfmpegPath,
            RestartRequired: state.RestartRequired));
    }

    private sealed record GeneratedHlsSettingsUpdateRequest(
        bool Enabled,
        string? FfmpegPath);

    private sealed record GeneratedHlsSettingsResponse(
        bool Enabled,
        string? FfmpegPath,
        bool FfmpegAvailable,
        string? FfmpegUnavailableReason,
        bool EffectivelyEnabled,
        string ConfiguredFfmpegPath,
        bool RestartRequired);

    // -------------------------------------------------------------------------
    // Refresh schedule
    // -------------------------------------------------------------------------

    private static async Task<Ok<RefreshScheduleResponse>> GetRefreshScheduleAsync(
        IRefreshScheduleService refreshScheduleService,
        CancellationToken cancellationToken)
    {
        var settings = await refreshScheduleService.GetSettingsAsync(cancellationToken);
        var nextRefresh = await refreshScheduleService.GetNextScheduledRefreshUtcAsync(cancellationToken);
        return TypedResults.Ok(new RefreshScheduleResponse(
            ScheduleKind: settings.ScheduleKind,
            StartupCatchup: settings.StartupCatchup,
            NextScheduledRefreshUtc: nextRefresh));
    }

    private static async Task<Results<Ok<RefreshScheduleResponse>, ValidationProblem>> UpdateRefreshScheduleAsync(
        RefreshScheduleUpdateRequest request,
        IRefreshScheduleService refreshScheduleService,
        CancellationToken cancellationToken)
    {
        var (succeeded, error) = await refreshScheduleService.UpdateAsync(
            new RefreshScheduleSettings(request.ScheduleKind, request.StartupCatchup), cancellationToken);

        if (!succeeded)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["refreshSchedule"] = [error ?? "Failed to update refresh schedule."],
            });
        }

        var settings = await refreshScheduleService.GetSettingsAsync(cancellationToken);
        var nextRefresh = await refreshScheduleService.GetNextScheduledRefreshUtcAsync(cancellationToken);
        return TypedResults.Ok(new RefreshScheduleResponse(
            ScheduleKind: settings.ScheduleKind,
            StartupCatchup: settings.StartupCatchup,
            NextScheduledRefreshUtc: nextRefresh));
    }

    private sealed record RefreshScheduleUpdateRequest(string ScheduleKind, bool StartupCatchup);
    private sealed record RefreshScheduleResponse(string ScheduleKind, bool StartupCatchup, DateTime? NextScheduledRefreshUtc);

    // -------------------------------------------------------------------------
    // HDHomeRun settings
    // -------------------------------------------------------------------------

    private static async Task<Ok<HdhrSettingsResponse>> GetHdhrSettingsAsync(
        IHdHomeRunSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var state = await settingsService.GetSettingsAsync(cancellationToken);
        return TypedResults.Ok(MapHdhrResponse(state));
    }

    private static async Task<Results<Ok<HdhrSettingsResponse>, ValidationProblem>> UpdateHdhrSettingsAsync(
        HdhrSettingsUpdateRequest request,
        IHdHomeRunSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var result = await settingsService.UpdateAsync(new UpdateHdhrSettingsCommand(
            Enabled: request.Enabled,
            TunerCountOverride: request.TunerCountOverride,
            AdvertisedBaseUrl: request.AdvertisedBaseUrl,
            DiscoveryEnabled: request.DiscoveryEnabled,
            SsdpEnabled: request.SsdpEnabled,
            SiliconDustDiscoveryEnabled: request.SiliconDustDiscoveryEnabled), cancellationToken);

        if (!result.Succeeded)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["hdhr"] = [result.Error ?? "HDHR settings update failed."],
            });
        }

        var state = await settingsService.GetSettingsAsync(cancellationToken);
        return TypedResults.Ok(MapHdhrResponse(state));
    }

    private static HdhrSettingsResponse MapHdhrResponse(HdhrSettingsState state) =>
        new(
            Enabled: state.Saved.Enabled,
            EffectiveTunerCount: state.Saved.EffectiveTunerCount,
            TunerCountOverride: state.Saved.TunerCountOverride,
            ProviderTunerLimit: state.Saved.ProviderTunerLimit,
            IsStreamLimitEnforced: state.Saved.IsStreamLimitEnforced,
            AdvertisedBaseUrl: state.Saved.AdvertisedBaseUrl,
            ResolvedBaseUrl: state.Saved.ResolvedBaseUrl,
            DiscoveryEnabled: state.Saved.DiscoveryEnabled,
            SsdpEnabled: state.Saved.SsdpEnabled,
            SiliconDustDiscoveryEnabled: state.Saved.SiliconDustDiscoveryEnabled,
            RestartRequired: state.RestartRequired,
            DisabledByEnvironment: state.DisabledByEnvironment);

    private sealed record HdhrSettingsUpdateRequest(
        bool Enabled,
        int? TunerCountOverride,
        string? AdvertisedBaseUrl,
        bool DiscoveryEnabled,
        bool SsdpEnabled,
        bool SiliconDustDiscoveryEnabled);

    private sealed record HdhrSettingsResponse(
        bool Enabled,
        int EffectiveTunerCount,
        int? TunerCountOverride,
        int? ProviderTunerLimit,
        bool IsStreamLimitEnforced,
        string? AdvertisedBaseUrl,
        string ResolvedBaseUrl,
        bool DiscoveryEnabled,
        bool SsdpEnabled,
        bool SiliconDustDiscoveryEnabled,
        bool RestartRequired,
        bool DisabledByEnvironment);
}
