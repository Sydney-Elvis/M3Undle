using M3Undle.Web.Application;

namespace M3Undle.Web.Security;

/// <summary>
/// Endpoint filter for Xtream-style streaming URLs where credentials are embedded
/// in the path: <c>/live/{xtreamUser}/{xtreamPass}/{streamId}</c>.
///
/// Reads the <c>xtreamUser</c> and <c>xtreamPass</c> route values, validates them
/// against the same credential store as the standard query-string/Basic-Auth filter,
/// and stores the resolved access on the <see cref="HttpContext"/> so downstream
/// handlers can call <see cref="ClientAccessHttpContextExtensions.GetResolvedClientAccess"/>.
/// </summary>
internal sealed class XtreamPathCredentialFilter(
    IEndpointSecurityService endpointSecurityService,
    ICredentialValidator credentialValidator,
    IProfileResolver profileResolver)
    : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        var username = (string?)http.GetRouteValue("xtreamUser") ?? string.Empty;
        var password = (string?)http.GetRouteValue("xtreamPass") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Results.Unauthorized();
        }

        var securityEnabled = await endpointSecurityService.IsEnabledAsync(http.RequestAborted);

        ResolvedClientAccess access;

        if (!securityEnabled)
        {
            var profileId = await profileResolver.ResolveActiveProfileIdAsync(null, http.RequestAborted);
            if (string.IsNullOrWhiteSpace(profileId))
                return Results.Problem("No active profile is available for endpoint delivery.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var fallback = new AccessCredential(
                Id: "endpoint-auth-disabled",
                Username: "anonymous",
                PasswordHash: string.Empty,
                Enabled: true,
                AuthType: AccessCredentialAuthType.UsernamePassword);

            access = new ResolvedClientAccess(
                Credential: fallback,
                Binding: new AccessBinding(fallback.Id, profileId, [profileId], "hdhr-main"),
                Transport: ClientCredentialTransport.None,
                UrlCredential: null);
        }
        else
        {
            var credential = await credentialValidator.ValidateAsync(username, password, http.RequestAborted);
            if (credential is null)
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Results.Unauthorized();
            }

            var binding = await endpointSecurityService.GetBindingAsync(credential.Id, http.RequestAborted);
            var activeProfileId = await profileResolver.ResolveActiveProfileIdAsync(binding?.ActiveProfileId, http.RequestAborted);
            if (string.IsNullOrWhiteSpace(activeProfileId))
                return Results.Problem("No active profile is available for endpoint delivery.", statusCode: StatusCodes.Status503ServiceUnavailable);

            access = new ResolvedClientAccess(
                Credential: credential,
                Binding: new AccessBinding(
                    CredentialId: credential.Id,
                    ActiveProfileId: activeProfileId,
                    AllowedProfileIds: [activeProfileId],
                    VirtualTunerId: binding?.VirtualTunerId ?? "hdhr-main"),
                Transport: ClientCredentialTransport.QueryString,
                UrlCredential: new AccessUrlCredential(username, password));
        }

        http.SetResolvedClientAccess(access);
        return await next(context);
    }
}
