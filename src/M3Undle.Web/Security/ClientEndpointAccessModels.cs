namespace M3Undle.Web.Security;

public enum AccessCredentialAuthType
{
    UsernamePassword = 0,
    Token = 1,
}

public enum ClientCredentialTransport
{
    None = 0,
    AuthorizationHeaderBasic = 1,
    QueryString = 2,
}

public enum ClientAccessFailureReason
{
    None = 0,
    MissingCredentials = 1,
    InvalidCredentials = 2,
    NoActiveProfile = 3,
}

public sealed record AccessCredential(
    string Id,
    string Username,
    string PasswordHash,
    bool Enabled,
    AccessCredentialAuthType AuthType);

public sealed record AccessBinding(
    string CredentialId,
    string ActiveProfileId,
    IReadOnlyList<string> AllowedProfileIds,
    string? VirtualTunerId);

public sealed record AccessUrlCredential(string Username, string Password);

public sealed record ResolvedClientAccess(
    AccessCredential Credential,
    AccessBinding Binding,
    ClientCredentialTransport Transport,
    AccessUrlCredential? UrlCredential);

public sealed record ClientAccessResolutionResult(
    ResolvedClientAccess? Access,
    ClientAccessFailureReason FailureReason)
{
    public bool IsSuccess => Access is not null;

    public static ClientAccessResolutionResult Success(ResolvedClientAccess access)
        => new(access, ClientAccessFailureReason.None);

    public static ClientAccessResolutionResult Fail(ClientAccessFailureReason reason)
        => new(null, reason);
}
