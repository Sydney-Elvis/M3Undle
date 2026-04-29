using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Observability;

public interface IMetricsTokenService
{
    Task<IReadOnlyList<MetricsTokenSummary>> ListAsync(CancellationToken ct = default);
    Task<CreateMetricsTokenResult> CreateAsync(CreateMetricsTokenCommand command, CancellationToken ct = default);
    Task<bool> DeleteAsync(string metricsTokenId, CancellationToken ct = default);
    Task<bool> TryAuthenticateAsync(string? token, CancellationToken ct = default);
}

public sealed class MetricsTokenService(ApplicationDbContext db, TimeProvider timeProvider) : IMetricsTokenService
{
    private readonly PasswordHasher<string> _passwordHasher = new();

    public async Task<IReadOnlyList<MetricsTokenSummary>> ListAsync(CancellationToken ct = default)
        => await db.MetricsTokens
            .AsNoTracking()
            .OrderBy(x => x.CreatedUtc)
            .Select(x => new MetricsTokenSummary(
                x.MetricsTokenId,
                x.Name,
                x.Scope,
                x.CreatedUtc,
                x.LastUsedUtc,
                x.ExpiresUtc,
                x.ExpiresUtc.HasValue && x.ExpiresUtc.Value <= timeProvider.GetUtcNow().UtcDateTime))
            .ToArrayAsync(ct);

    public async Task<CreateMetricsTokenResult> CreateAsync(CreateMetricsTokenCommand command, CancellationToken ct = default)
    {
        var name = command.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return new(false, "Token name is required.", null, null);

        if (name.Length > 128)
            return new(false, "Token name must be 128 characters or fewer.", null, null);

        if (command.ExpiresUtc is { } expiresUtc && expiresUtc <= timeProvider.GetUtcNow().UtcDateTime)
            return new(false, "Expiration must be in the future.", null, null);

        var duplicate = await db.MetricsTokens
            .AsNoTracking()
            .AnyAsync(x => x.Name == name, ct);
        if (duplicate)
            return new(false, $"A metrics token named '{name}' already exists.", null, null);

        var plainToken = GenerateToken();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entity = new MetricsToken
        {
            MetricsTokenId = Guid.NewGuid().ToString("N"),
            Name = name,
            TokenHash = _passwordHasher.HashPassword(name, plainToken),
            Scope = "metrics:read",
            CreatedUtc = now,
            ExpiresUtc = command.ExpiresUtc,
        };

        db.MetricsTokens.Add(entity);
        await db.SaveChangesAsync(ct);

        var summary = new MetricsTokenSummary(
            entity.MetricsTokenId,
            entity.Name,
            entity.Scope,
            entity.CreatedUtc,
            entity.LastUsedUtc,
            entity.ExpiresUtc,
            IsExpired: false);

        return new(true, null, summary, plainToken);
    }

    public async Task<bool> DeleteAsync(string metricsTokenId, CancellationToken ct = default)
    {
        var entity = await db.MetricsTokens.FirstOrDefaultAsync(x => x.MetricsTokenId == metricsTokenId, ct);
        if (entity is null)
            return false;

        db.MetricsTokens.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> TryAuthenticateAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var tokens = await db.MetricsTokens
            .Where(x => x.ExpiresUtc == null || x.ExpiresUtc > now)
            .OrderBy(x => x.CreatedUtc)
            .ToArrayAsync(ct);

        foreach (var candidate in tokens)
        {
            var result = _passwordHasher.VerifyHashedPassword(candidate.Name, candidate.TokenHash, token.Trim());
            if (result == PasswordVerificationResult.Failed)
                continue;

            candidate.LastUsedUtc = now;
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
                candidate.TokenHash = _passwordHasher.HashPassword(candidate.Name, token.Trim());

            await db.SaveChangesAsync(CancellationToken.None);
            return true;
        }

        return false;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

public sealed record MetricsTokenSummary(
    string MetricsTokenId,
    string Name,
    string Scope,
    DateTime CreatedUtc,
    DateTime? LastUsedUtc,
    DateTime? ExpiresUtc,
    bool IsExpired);

public sealed record CreateMetricsTokenCommand(string? Name, DateTime? ExpiresUtc);

public sealed record CreateMetricsTokenResult(
    bool Succeeded,
    string? Error,
    MetricsTokenSummary? Token,
    string? PlainToken);
