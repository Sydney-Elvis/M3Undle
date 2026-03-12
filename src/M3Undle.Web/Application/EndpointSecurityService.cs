using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public sealed record EndpointSecuritySettings(
    bool Enabled,
    string? Username,
    bool HasCredential,
    string? ActiveProfileId,
    string? VirtualTunerId);

public sealed record EndpointBindingState(string? ActiveProfileId, string? VirtualTunerId);

public sealed record UpdateEndpointSecurityCommand(
    bool Enabled,
    string? Username,
    string? Password,
    string? ActiveProfileId);

public sealed record EndpointSecurityUpdateResult(
    bool Succeeded,
    string? Error,
    EndpointSecuritySettings Settings);

public interface IEndpointSecurityService
{
    ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken);
    Task<EndpointSecuritySettings> GetSettingsAsync(CancellationToken cancellationToken);
    Task<EndpointBindingState?> GetBindingAsync(string credentialId, CancellationToken cancellationToken);
    Task<EndpointSecurityUpdateResult> UpdateAsync(UpdateEndpointSecurityCommand command, CancellationToken cancellationToken);
}

public sealed class EndpointSecurityService(ApplicationDbContext db) : IEndpointSecurityService
{
    private readonly PasswordHasher<string> _passwordHasher = new();

    public async ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        var site = await EnsureSiteSettingsAsync(cancellationToken);
        return site.EndpointSecurityEnabled;
    }

    public async Task<EndpointSecuritySettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var site = await EnsureSiteSettingsAsync(cancellationToken);
        var credential = await db.EndpointCredentials
            .AsNoTracking()
            .OrderBy(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        EndpointAccessBinding? binding = null;
        if (credential is not null)
        {
            binding = await db.EndpointAccessBindings
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.EndpointCredentialId == credential.EndpointCredentialId, cancellationToken);
        }

        return new EndpointSecuritySettings(
            Enabled: site.EndpointSecurityEnabled,
            Username: credential?.Username,
            HasCredential: credential is not null,
            ActiveProfileId: binding?.ActiveProfileId,
            VirtualTunerId: binding?.VirtualTunerId);
    }

    public async Task<EndpointBindingState?> GetBindingAsync(string credentialId, CancellationToken cancellationToken)
    {
        var binding = await db.EndpointAccessBindings
            .AsNoTracking()
            .Where(x => x.Enabled && x.EndpointCredentialId == credentialId)
            .Select(x => new EndpointBindingState(x.ActiveProfileId, x.VirtualTunerId))
            .FirstOrDefaultAsync(cancellationToken);

        return binding;
    }

    public async Task<EndpointSecurityUpdateResult> UpdateAsync(UpdateEndpointSecurityCommand command, CancellationToken cancellationToken)
    {
        var site = await EnsureSiteSettingsAsync(cancellationToken);

        var credential = await db.EndpointCredentials
            .OrderBy(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        // TODO: remove this guard when multi-credential support is added.
        var hasMultiple = await db.EndpointCredentials.Skip(1).AnyAsync(cancellationToken);
        if (hasMultiple)
            return await BuildFailureAsync("Multiple endpoint credentials exist. Only one endpoint credential is supported at this time.", cancellationToken);

        var now = DateTime.UtcNow;

        var shouldCreateCredential = credential is null &&
                                     (command.Enabled ||
                                      !string.IsNullOrWhiteSpace(command.Username) ||
                                      !string.IsNullOrWhiteSpace(command.Password));

        if (shouldCreateCredential)
        {
            if (string.IsNullOrWhiteSpace(command.Username))
            {
                return await BuildFailureAsync("username is required when creating endpoint credentials.", cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(command.Password))
            {
                return await BuildFailureAsync("password is required when creating endpoint credentials.", cancellationToken);
            }

            var username = command.Username.Trim();
            var normalized = NormalizeUsername(username);
            var duplicate = await db.EndpointCredentials
                .AsNoTracking()
                .AnyAsync(x => x.NormalizedUsername == normalized, cancellationToken);
            if (duplicate)
                return await BuildFailureAsync($"username '{username}' is already in use.", cancellationToken);

            credential = new EndpointCredential
            {
                EndpointCredentialId = Guid.NewGuid().ToString(),
                Username = username,
                NormalizedUsername = normalized,
                PasswordHash = _passwordHasher.HashPassword("endpoint", command.Password),
                Enabled = command.Enabled,
                AuthType = "password",
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            db.EndpointCredentials.Add(credential);
        }
        else if (credential is not null)
        {
            if (!string.IsNullOrWhiteSpace(command.Username))
            {
                var username = command.Username.Trim();
                var normalized = NormalizeUsername(username);
                var duplicate = await db.EndpointCredentials
                    .AsNoTracking()
                    .AnyAsync(x => x.NormalizedUsername == normalized && x.EndpointCredentialId != credential.EndpointCredentialId, cancellationToken);
                if (duplicate)
                    return await BuildFailureAsync($"username '{username}' is already in use.", cancellationToken);

                credential.Username = username;
                credential.NormalizedUsername = normalized;
                credential.UpdatedUtc = now;
            }

            if (!string.IsNullOrWhiteSpace(command.Password))
            {
                credential.PasswordHash = _passwordHasher.HashPassword("endpoint", command.Password);
                credential.UpdatedUtc = now;
            }

            credential.Enabled = command.Enabled;
        }

        if (command.Enabled && credential is null)
            return await BuildFailureAsync("Endpoint credential is not configured.", cancellationToken);

        if (command.Enabled && credential is { PasswordHash.Length: 0 })
            return await BuildFailureAsync("Endpoint credential password is not configured.", cancellationToken);

        if (!string.IsNullOrWhiteSpace(command.ActiveProfileId))
        {
            var profileExists = await db.Profiles
                .AsNoTracking()
                .AnyAsync(x => x.ProfileId == command.ActiveProfileId && x.Enabled, cancellationToken);
            if (!profileExists)
                return await BuildFailureAsync("The selected active profile does not exist or is disabled.", cancellationToken);
        }

        if (credential is not null)
        {
            var binding = await db.EndpointAccessBindings
                .FirstOrDefaultAsync(x => x.EndpointCredentialId == credential.EndpointCredentialId, cancellationToken);

            if (binding is null)
            {
                binding = new EndpointAccessBinding
                {
                    EndpointAccessBindingId = Guid.NewGuid().ToString(),
                    EndpointCredentialId = credential.EndpointCredentialId,
                    VirtualTunerId = "hdhr-main",
                    Enabled = true,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                };
                db.EndpointAccessBindings.Add(binding);
            }

            if (command.ActiveProfileId is not null)
                binding.ActiveProfileId = string.IsNullOrWhiteSpace(command.ActiveProfileId) ? null : command.ActiveProfileId.Trim();

            binding.Enabled = command.Enabled;
            binding.UpdatedUtc = now;
        }

        site.EndpointSecurityEnabled = command.Enabled;
        await db.SaveChangesAsync(cancellationToken);

        var settings = await GetSettingsAsync(cancellationToken);
        return new EndpointSecurityUpdateResult(true, null, settings);
    }

    private async Task<EndpointSecurityUpdateResult> BuildFailureAsync(string message, CancellationToken cancellationToken)
        => new(false, message, await GetSettingsAsync(cancellationToken));

    private async Task<SiteSettings> EnsureSiteSettingsAsync(CancellationToken cancellationToken)
    {
        var site = await db.SiteSettings.FirstOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (site is not null)
            return site;

        site = new SiteSettings
        {
            Id = 1,
            AuthenticationEnabled = false,
            EndpointSecurityEnabled = false,
        };
        db.SiteSettings.Add(site);
        await db.SaveChangesAsync(cancellationToken);
        return site;
    }

    internal static string NormalizeUsername(string username) => username.Trim().ToUpperInvariant();
}
