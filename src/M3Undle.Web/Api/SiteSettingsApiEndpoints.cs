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
            ActiveProfileId: request.ActiveProfileId), cancellationToken);

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
        string? ActiveProfileId);

    private sealed record EndpointSecurityResponse(
        bool Enabled,
        string? Username,
        bool HasCredential,
        string? ActiveProfileId,
        string? VirtualTunerId);
}
