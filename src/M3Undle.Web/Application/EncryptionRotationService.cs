using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public sealed record EncryptionStatusResult(
    bool EncryptionAvailable,
    string? ActiveKeyId,
    IReadOnlyList<string> KnownKeyIds,
    int ProvidersOnActiveKey,
    int ProvidersOnOtherKey,
    int DownstreamIntegrationsOnActiveKey,
    int DownstreamIntegrationsOnOtherKey);

public sealed record RotateResult(
    bool Success,
    string? ErrorMessage,
    string? ActiveKeyId,
    string? BackupFilePath,
    int ProvidersMigrated,
    int DownstreamIntegrationsMigrated,
    int RowsAlreadyCurrent);

/// <summary>
/// Bulk re-encrypts stored secrets (Provider Xtream passwords, DownstreamIntegration API keys)
/// under the active key from SecretEncryptionService's key ring. See docs/DOCKER.md for the
/// operator rotation workflow.
/// </summary>
public sealed class EncryptionRotationService(
    ApplicationDbContext db,
    SecretEncryptionService encryption,
    SqliteBackupService backupService,
    ILogger<EncryptionRotationService> logger)
{
    public async Task<EncryptionStatusResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        var activeKeyId = encryption.ActiveKeyId;

        var providerValues = await db.Providers
            .AsNoTracking()
            .Where(p => p.XtreamEncryptedPassword != null)
            .Select(p => p.XtreamEncryptedPassword!)
            .ToListAsync(cancellationToken);

        var downstreamValues = await db.DownstreamIntegrations
            .AsNoTracking()
            .Where(d => d.ApiKeyEncrypted != null)
            .Select(d => d.ApiKeyEncrypted!)
            .ToListAsync(cancellationToken);

        var (providersOnActive, providersOnOther) = CountByActiveKey(providerValues, activeKeyId);
        var (downstreamOnActive, downstreamOnOther) = CountByActiveKey(downstreamValues, activeKeyId);

        return new EncryptionStatusResult(
            EncryptionAvailable: encryption.IsAvailable,
            ActiveKeyId: activeKeyId,
            KnownKeyIds: encryption.KnownKeyIds,
            ProvidersOnActiveKey: providersOnActive,
            ProvidersOnOtherKey: providersOnOther,
            DownstreamIntegrationsOnActiveKey: downstreamOnActive,
            DownstreamIntegrationsOnOtherKey: downstreamOnOther);
    }

    public async Task<RotateResult> RotateAsync(CancellationToken cancellationToken)
    {
        if (!encryption.IsAvailable)
            return new RotateResult(Success: false, ErrorMessage: "No encryption key is configured.", ActiveKeyId: null, BackupFilePath: null, 0, 0, 0);

        var activeKeyId = encryption.ActiveKeyId!;

        var providersNeedingMigration = await db.Providers
            .Where(p => p.XtreamEncryptedPassword != null)
            .ToListAsync(cancellationToken);
        var downstreamNeedingMigration = await db.DownstreamIntegrations
            .Where(d => d.ApiKeyEncrypted != null)
            .ToListAsync(cancellationToken);

        var providersToMigrate = providersNeedingMigration
            .Where(p => encryption.Peek(p.XtreamEncryptedPassword!).KeyId != activeKeyId)
            .ToList();
        var downstreamToMigrate = downstreamNeedingMigration
            .Where(d => encryption.Peek(d.ApiKeyEncrypted!).KeyId != activeKeyId)
            .ToList();

        var rowsAlreadyCurrent = (providersNeedingMigration.Count - providersToMigrate.Count)
            + (downstreamNeedingMigration.Count - downstreamToMigrate.Count);

        if (providersToMigrate.Count == 0 && downstreamToMigrate.Count == 0)
        {
            return new RotateResult(
                Success: true,
                ErrorMessage: null,
                ActiveKeyId: activeKeyId,
                BackupFilePath: null,
                ProvidersMigrated: 0,
                DownstreamIntegrationsMigrated: 0,
                RowsAlreadyCurrent: rowsAlreadyCurrent);
        }

        var backupPath = await backupService.BackupAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var provider in providersToMigrate)
            {
                var plaintext = encryption.Decrypt(provider.XtreamEncryptedPassword!);
                provider.XtreamEncryptedPassword = encryption.Encrypt(plaintext);
                provider.UpdatedUtc = DateTime.UtcNow;
            }

            foreach (var integration in downstreamToMigrate)
            {
                var plaintext = encryption.Decrypt(integration.ApiKeyEncrypted!);
                integration.ApiKeyEncrypted = encryption.Encrypt(plaintext);
                integration.UpdatedUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Encryption key rotation aborted — a row failed to decrypt. Rolling back; no rows were changed.");
            await transaction.RollbackAsync(cancellationToken);
            return new RotateResult(
                Success: false,
                ErrorMessage: "Rotation aborted: at least one stored value failed to decrypt under any key in the ring. No rows were changed. See logs for details.",
                ActiveKeyId: activeKeyId,
                BackupFilePath: backupPath,
                ProvidersMigrated: 0,
                DownstreamIntegrationsMigrated: 0,
                RowsAlreadyCurrent: rowsAlreadyCurrent);
        }

        logger.LogInformation(
            "Encryption key rotation complete: {ProviderCount} provider(s) and {DownstreamCount} downstream integration(s) migrated to key '{ActiveKeyId}'.",
            providersToMigrate.Count, downstreamToMigrate.Count, activeKeyId);

        return new RotateResult(
            Success: true,
            ErrorMessage: null,
            ActiveKeyId: activeKeyId,
            BackupFilePath: backupPath,
            ProvidersMigrated: providersToMigrate.Count,
            DownstreamIntegrationsMigrated: downstreamToMigrate.Count,
            RowsAlreadyCurrent: rowsAlreadyCurrent);
    }

    private (int OnActive, int OnOther) CountByActiveKey(IReadOnlyList<string> values, string? activeKeyId)
    {
        var onActive = 0;
        var onOther = 0;
        foreach (var value in values)
        {
            // activeKeyId is null when no key is configured at all — in that case nothing can
            // be "on the active key" (there isn't one), including legacy rows whose own
            // Peek().KeyId is also null. Guard explicitly so null never matches null.
            if (activeKeyId is not null && encryption.Peek(value).KeyId == activeKeyId)
                onActive++;
            else
                onOther++;
        }

        return (onActive, onOther);
    }
}
