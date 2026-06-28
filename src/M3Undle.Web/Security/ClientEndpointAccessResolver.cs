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

            // Read credentials from the request for echo-back only — no validation is performed.
            // Clients (e.g. Smarters Pro on TV) verify that the returned username matches what
            // they sent; returning "anonymous" causes them to reject the connection.
            var requestCredentials = await TryReadCredentialsAsync(context, cancellationToken);
            var fallbackUrlCredential = requestCredentials.HasValue
                ? new AccessUrlCredential(requestCredentials.Value.Username, requestCredentials.Value.Password)
                : null;

            return ClientAccessResolutionResult.Success(new ResolvedClientAccess(
                Credential: fallbackCredential,
                Binding: new AccessBinding(
                    CredentialId: fallbackCredential.Id,
                    ActiveProfileId: profileId,
                    AllowedProfileIds: [profileId],
                    VirtualTunerId: "hdhr-main"),
                Transport: ClientCredentialTransport.None,
                UrlCredential: fallbackUrlCredential));
        }

        var credentials = await TryReadCredentialsAsync(context, cancellationToken);
        if (credentials is null)
            return ClientAccessResolutionResult.Fail(ClientAccessFailureReason.MissingCredentials);

        var (username, password, transport) = credentials.Value;
        var credential = await credentialValidator.ValidateAsync(username, password, cancellationToken);
        if (credential is null)
            return ClientAccessResolutionResult.Fail(ClientAccessFailureReason.InvalidCredentials);

        var bindingState = await endpointSecurityService.GetBindingAsync(credential.Id, cancellationToken);
        var activeProfileId = await profileResolver.ResolveActiveProfileIdAsync(bindingState?.ActiveProfileId, cancellationToken);
        if (string.IsNullOrWhiteSpace(activeProfileId))
            return ClientAccessResolutionResult.Fail(ClientAccessFailureReason.NoActiveProfile);

        var urlCredential = transport is ClientCredentialTransport.QueryString or ClientCredentialTransport.Form
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

    private static async ValueTask<(string Username, string Password, ClientCredentialTransport Transport)?> TryReadCredentialsAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        string username;
        string password;
        if (TryReadBasicHeaderCredentials(context.Request, out username, out password))
            return (username, password, ClientCredentialTransport.AuthorizationHeaderBasic);

        if (TryReadQueryCredentials(context.Request, out username, out password))
            return (username, password, ClientCredentialTransport.QueryString);

        var form = await TryReadFormAsync(context.Request, cancellationToken);
        if (form is not null && TryReadNameValueCredentials(form, out username, out password))
            return (username, password, ClientCredentialTransport.Form);

        return null;
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

        return TryReadNameValueCredentials(request.Query, out username, out password);
    }

    private static bool TryReadNameValueCredentials(
        IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> values,
        out string username,
        out string password)
    {
        username = string.Empty;
        password = string.Empty;

        foreach (var (userKey, passKey) in new (string User, string Pass)[]
                 {
                     ("username", "password"),
                     ("user", "pass"),
                     ("u", "p"),
                 })
        {
            if (!TryGetValue(values, userKey, out var userValues) || !TryGetValue(values, passKey, out var passValues))
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

    private static bool TryGetValue(
        IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> values,
        string key,
        out Microsoft.Extensions.Primitives.StringValues value)
    {
        foreach (var pair in values)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static async Task<IFormCollection?> TryReadFormAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return null;

        try
        {
            return await request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
