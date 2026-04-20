using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public interface IDownstreamIntegrationService
{
    Task<List<DownstreamIntegrationDto>> GetAllAsync(CancellationToken ct = default);
    Task<DownstreamIntegrationDto?> GetAsync(string id, CancellationToken ct = default);
    Task<(DownstreamIntegrationDto? Result, string? Error)> CreateAsync(CreateDownstreamIntegrationRequest request, CancellationToken ct = default);
    Task<(DownstreamIntegrationDto? Result, string? Error)> UpdateAsync(string id, UpdateDownstreamIntegrationRequest request, CancellationToken ct = default);
    Task<string?> DeleteAsync(string id, CancellationToken ct = default);
}

public sealed class DownstreamIntegrationService(
    IServiceScopeFactory scopeFactory,
    SecretEncryptionService encryption) : IDownstreamIntegrationService
{
    private static readonly string[] ValidKinds = ["jellyfin", "emby", "webhook"];

    public async Task<List<DownstreamIntegrationDto>> GetAllAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var rows = await db.DownstreamIntegrations
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        return rows.Select(ToDto).ToList();
    }

    public async Task<DownstreamIntegrationDto?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var row = await db.DownstreamIntegrations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.DownstreamIntegrationId == id, ct);

        return row is null ? null : ToDto(row);
    }

    public async Task<(DownstreamIntegrationDto? Result, string? Error)> CreateAsync(
        CreateDownstreamIntegrationRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return (null, "Name is required.");

        if (!ValidKinds.Contains(request.Kind))
            return (null, $"Invalid kind '{request.Kind}'. Valid values: {string.Join(", ", ValidKinds)}.");

        if (string.IsNullOrWhiteSpace(request.BaseUrl))
            return (null, "Base URL is required.");

        string? apiKeyEncrypted = null;
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            if (!encryption.IsAvailable)
                return (null, "API key storage requires M3UNDLE_ENCRYPTION_KEY to be configured.");
            apiKeyEncrypted = encryption.Encrypt(request.ApiKey);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var now = DateTime.UtcNow;
        var row = new DownstreamIntegration
        {
            DownstreamIntegrationId = Guid.NewGuid().ToString("N"),
            ProfileId = string.IsNullOrWhiteSpace(request.ProfileId) ? null : request.ProfileId,
            Name = request.Name.Trim(),
            Kind = request.Kind,
            BaseUrl = request.BaseUrl.TrimEnd('/'),
            ApiKeyEncrypted = apiKeyEncrypted,
            WebhookHeadersJson = string.IsNullOrWhiteSpace(request.WebhookHeadersJson) ? null : request.WebhookHeadersJson,
            TriggerOnLineupUpdate = request.TriggerOnLineupUpdate,
            TriggerOnGuideUpdate = request.TriggerOnGuideUpdate,
            Enabled = request.Enabled,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        db.DownstreamIntegrations.Add(row);
        await db.SaveChangesAsync(ct);

        return (ToDto(row), null);
    }

    public async Task<(DownstreamIntegrationDto? Result, string? Error)> UpdateAsync(
        string id, UpdateDownstreamIntegrationRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return (null, "Name is required.");

        if (string.IsNullOrWhiteSpace(request.BaseUrl))
            return (null, "Base URL is required.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var row = await db.DownstreamIntegrations
            .FirstOrDefaultAsync(x => x.DownstreamIntegrationId == id, ct);

        if (row is null)
            return (null, "Integration not found.");

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            if (!encryption.IsAvailable)
                return (null, "API key storage requires M3UNDLE_ENCRYPTION_KEY to be configured.");
            row.ApiKeyEncrypted = encryption.Encrypt(request.ApiKey);
        }
        else if (request.ClearApiKey)
        {
            row.ApiKeyEncrypted = null;
        }

        row.Name = request.Name.Trim();
        row.BaseUrl = request.BaseUrl.TrimEnd('/');
        row.WebhookHeadersJson = string.IsNullOrWhiteSpace(request.WebhookHeadersJson) ? null : request.WebhookHeadersJson;
        row.TriggerOnLineupUpdate = request.TriggerOnLineupUpdate;
        row.TriggerOnGuideUpdate = request.TriggerOnGuideUpdate;
        row.Enabled = request.Enabled;
        row.UpdatedUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        return (ToDto(row), null);
    }

    public async Task<string?> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var row = await db.DownstreamIntegrations
            .FirstOrDefaultAsync(x => x.DownstreamIntegrationId == id, ct);

        if (row is null)
            return "Integration not found.";

        db.DownstreamIntegrations.Remove(row);
        await db.SaveChangesAsync(ct);
        return null;
    }

    private static DownstreamIntegrationDto ToDto(DownstreamIntegration x) =>
        new()
        {
            DownstreamIntegrationId = x.DownstreamIntegrationId,
            ProfileId = x.ProfileId,
            Name = x.Name,
            Kind = x.Kind,
            BaseUrl = x.BaseUrl,
            HasApiKey = x.ApiKeyEncrypted is not null,
            WebhookHeadersJson = x.WebhookHeadersJson,
            TriggerOnLineupUpdate = x.TriggerOnLineupUpdate,
            TriggerOnGuideUpdate = x.TriggerOnGuideUpdate,
            Enabled = x.Enabled,
            LastNotifiedUtc = x.LastNotifiedUtc,
            LastNotifyError = x.LastNotifyError,
        };
}
