using Microsoft.AspNetCore.Http;

namespace M3Undle.Web.Security;

public interface ICredentialValidator
{
    ValueTask<AccessCredential?> ValidateAsync(string username, string password, CancellationToken cancellationToken);
}

public interface IProfileResolver
{
    ValueTask<string?> ResolveActiveProfileIdAsync(string? preferredProfileId, CancellationToken cancellationToken);
}

public interface IAccessResolver
{
    ValueTask<ClientAccessResolutionResult> ResolveAsync(HttpContext context, CancellationToken cancellationToken);
}
