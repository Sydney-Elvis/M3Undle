using M3Undle.Web.Application;
using M3Undle.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Security;

internal sealed class DbCredentialValidator(ApplicationDbContext db) : ICredentialValidator
{
    private readonly PasswordHasher<string> _passwordHasher = new();

    public async ValueTask<AccessCredential?> ValidateAsync(string username, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedUsername = EndpointSecurityService.NormalizeUsername(username);
        var entity = await db.EndpointCredentials
            .AsNoTracking()
            .Where(x => x.Enabled && x.AuthType == "password" && x.NormalizedUsername == normalizedUsername)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity is null)
            return null;

        if (string.IsNullOrWhiteSpace(password))
            return null;

        var verify = _passwordHasher.VerifyHashedPassword("endpoint", entity.PasswordHash, password);
        if (verify is PasswordVerificationResult.Failed)
            return null;

        return new AccessCredential(
            Id: entity.EndpointCredentialId,
            Username: entity.Username,
            PasswordHash: entity.PasswordHash,
            Enabled: entity.Enabled,
            AuthType: AccessCredentialAuthType.UsernamePassword);
    }
}
