using M3Undle.Web.Application;
using M3Undle.Web.Security;

namespace M3Undle.Web.Api;

public static class SiteSettingsApiEndpoints
{
    public static IEndpointRouteBuilder MapSiteSettingsApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/settings");
        group.RequireAuthorization(UiAccessPolicy.Name);

        group.MapGet("/endpoint-security", GetEndpointSecurityAsync);
        group.MapPut("/endpoint-security", UpdateEndpointSecurityAsync);
        group.MapGet("/generated-hls", GetGeneratedHlsSettingsAsync);
        group.MapPut("/generated-hls", UpdateGeneratedHlsSettingsAsync);

        return app;
    }

    private static async Task<IResult> GetEndpointSecurityAsync(
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

    private static async Task<IResult> UpdateEndpointSecurityAsync(
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

    private static async Task<IResult> GetGeneratedHlsSettingsAsync(
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

    private static async Task<IResult> UpdateGeneratedHlsSettingsAsync(
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
}
