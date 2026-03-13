using System.Net.Http.Headers;
using System.Text;
using M3Undle.Web.Application;
using Microsoft.AspNetCore.Http;

namespace M3Undle.Web.Security;

internal sealed class ClientEndpointAccessResolver(
    IEndpointSecurityService endpointSecurityService,
    ICredentialValidator credentialValidator,
    IProfileResolver profileResolver)
    : IAccessResolver
{
    public async ValueTask<ClientAccessResolutionResult> ResolveAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var endpointSecurityEnabled = await endpointSecurityService.IsEnabledAsync(cancellationToken);
        if (!endpointSecurityEnabled)
        {
            var profileId = await profileResolver.ResolveActiveProfileIdAsync(preferredProfileId: null, cancellationToken);
            if (string.IsNullOrWhiteSpace(profileId))
                return ClientAccessResolutionResult.Fail(ClientAccessFailureReason.NoActiveProfile);

            var fallbackCredential = new AccessCredential(
                Id: "endpoint-auth-disabled",
                Username: "anonymous",
                PasswordHash: string.Empty,
                Enabled: true,
                AuthType: AccessCredentialAuthType.UsernamePassword);

            return ClientAccessResolutionResult.Success(new ResolvedClientAccess(
                Credential: fallbackCredential,
                Binding: new AccessBinding(
                    CredentialId: fallbackCredential.Id,
                    ActiveProfileId: profileId,
                    AllowedProfileIds: [profileId],
                    VirtualTunerId: "hdhr-main"),
                Transport: ClientCredentialTransport.None,
                UrlCredential: null));
        }

        if (!TryReadCredentials(context, out var username, out var password, out var transport))
            return ClientAccessResolutionResult.Fail(ClientAccessFailureReason.MissingCredentials);

        var credential = await credentialValidator.ValidateAsync(username, password, cancellationToken);
        if (credential is null)
            return ClientAccessResolutionResult.Fail(ClientAccessFailureReason.InvalidCredentials);

        var bindingState = await endpointSecurityService.GetBindingAsync(credential.Id, cancellationToken);
        var activeProfileId = await profileResolver.ResolveActiveProfileIdAsync(bindingState?.ActiveProfileId, cancellationToken);
        if (string.IsNullOrWhiteSpace(activeProfileId))
            return ClientAccessResolutionResult.Fail(ClientAccessFailureReason.NoActiveProfile);

        var urlCredential = transport == ClientCredentialTransport.QueryString
            ? new AccessUrlCredential(username, password)
            : null;

        return ClientAccessResolutionResult.Success(new ResolvedClientAccess(
            Credential: credential,
            Binding: new AccessBinding(
                CredentialId: credential.Id,
                ActiveProfileId: activeProfileId,
                AllowedProfileIds: [activeProfileId],
                VirtualTunerId: bindingState?.VirtualTunerId ?? "hdhr-main"),
            Transport: transport,
            UrlCredential: urlCredential));
    }

    private static bool TryReadCredentials(
        HttpContext context,
        out string username,
        out string password,
        out ClientCredentialTransport transport)
    {
        if (TryReadBasicHeaderCredentials(context.Request, out username, out password))
        {
            transport = ClientCredentialTransport.AuthorizationHeaderBasic;
            return true;
        }

        if (TryReadQueryCredentials(context.Request, out username, out password))
        {
            transport = ClientCredentialTransport.QueryString;
            return true;
        }

        username = string.Empty;
        password = string.Empty;
        transport = ClientCredentialTransport.None;
        return false;
    }

    private static bool TryReadBasicHeaderCredentials(HttpRequest request, out string username, out string password)
    {
        username = string.Empty;
        password = string.Empty;

        if (!request.Headers.TryGetValue("Authorization", out var authValues))
            return false;

        if (!AuthenticationHeaderValue.TryParse(authValues.ToString(), out var headerValue))
            return false;

        if (!string.Equals(headerValue.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(headerValue.Parameter))
            return false;

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(headerValue.Parameter));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = decoded.IndexOf(':');
        if (separator <= 0)
            return false;

        username = decoded[..separator].Trim();
        password = decoded[(separator + 1)..];

        return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password);
    }

    private static bool TryReadQueryCredentials(HttpRequest request, out string username, out string password)
    {
        username = string.Empty;
        password = string.Empty;

        var query = request.Query;

        foreach (var (userKey, passKey) in new (string User, string Pass)[]
                 {
                     ("username", "password"),
                     ("user", "pass"),
                     ("u", "p"),
                 })
        {
            if (!query.TryGetValue(userKey, out var userValues) || !query.TryGetValue(passKey, out var passValues))
                continue;

            var candidateUser = userValues.ToString().Trim();
            var candidatePass = passValues.ToString();
            if (string.IsNullOrWhiteSpace(candidateUser) || string.IsNullOrEmpty(candidatePass))
                continue;

            username = candidateUser;
            password = candidatePass;
            return true;
        }

        return false;
    }
}
