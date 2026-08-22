using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using M3Undle.Core;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Backup;

/// <summary>
/// Exports and imports the small, logical settings graph used to seed a clean instance. Full
/// portable backups remain owned by <see cref="PortableBackupService"/> and are intentionally
/// not routed through this service.
/// </summary>
public sealed class SettingsArchiveService(
    ApplicationDbContext db,
    RuntimePaths runtimePaths,
    SecretEncryptionService encryption,
    AppBuildInfo buildInfo,
    ILogger<SettingsArchiveService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<SettingsArchiveResult> CreateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var document = await CreateDocumentAsync(cancellationToken);
            var documentBytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            var backupId = Guid.NewGuid().ToString("N");
            var createdUtc = DateTime.UtcNow;
            var hasEncryptedValues = document.Providers.Any(x => x.XtreamEncryptedPassword is not null)
                || document.DownstreamIntegrations.Any(x => x.ApiKeyEncrypted is not null);

            var appliedMigrations = await db.Database.GetAppliedMigrationsAsync(cancellationToken);
            var manifest = new SettingsArchiveManifest
            {
                FormatIdentifier = SettingsArchiveFormat.Identifier,
                FormatVersion = SettingsArchiveFormat.CurrentVersion,
                DocumentVersion = SettingsArchiveFormat.CurrentDocumentVersion,
                Scope = "settings",
                AppVersion = buildInfo.Version,
                SchemaVersion = appliedMigrations.LastOrDefault(),
                BackupId = backupId,
                CreatedUtc = createdUtc,
                EncryptionKeyId = hasEncryptedValues ? encryption.ActiveKeyId : null,
                EncryptionKeyFingerprint = hasEncryptedValues ? encryption.ActiveKeyFingerprint : null,
                SettingsEntities = ["SiteSettings", "Providers", "Profiles", "ProfileProviders", "DownstreamIntegrations"],
                SettingsSha256 = Convert.ToHexString(SHA256.HashData(documentBytes)).ToLowerInvariant(),
            };

            var backupsDir = Path.Combine(runtimePaths.DataDirectory, "backups");
            Directory.CreateDirectory(backupsDir);
            var workDir = Path.Combine(backupsDir, $".settings-work-{backupId}");
            Directory.CreateDirectory(workDir);
            try
            {
                var fileName = $"m3undle-settings-{createdUtc:yyyyMMdd-HHmmss}-{backupId[..8]}{PortableBackupFormat.ArchiveExtension}";
                var temporaryPath = Path.Combine(workDir, fileName);
                var finalPath = Path.Combine(backupsDir, fileName);

                using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
                {
                    WriteEntry(archive, SettingsArchiveFormat.ManifestEntryName, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
                    WriteEntry(archive, SettingsArchiveFormat.DocumentEntryName, documentBytes);
                }

                SetOwnerOnlyPermissions(temporaryPath);
                File.Move(temporaryPath, finalPath, overwrite: false);
                logger.LogInformation("Settings archive {BackupId} created at {Path}.", backupId, finalPath);
                return SettingsArchiveResult.Succeeded(finalPath, manifest);
            }
            finally
            {
                if (Directory.Exists(workDir))
                    Directory.Delete(workDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Settings archive creation failed.");
            return SettingsArchiveResult.Failed(ex.Message);
        }
    }

    public async Task<SettingsArchivePreflightResult> PreflightAsync(string archivePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath))
            return SettingsArchivePreflightResult.NotSettingsArchive();
        if (new FileInfo(archivePath).Length > PortableBackupFormat.MaxArchiveSizeBytes)
            return SettingsArchivePreflightResult.Failed(["Settings archive exceeds the configured size limit."]);

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > 2)
                return SettingsArchivePreflightResult.Failed(["Settings archive contains too many entries."]);
            if (archive.Entries.Any(x => x.FullName is not SettingsArchiveFormat.ManifestEntryName and not SettingsArchiveFormat.DocumentEntryName))
                return SettingsArchivePreflightResult.Failed(["Settings archive contains an unexpected entry."]);
            var manifestBytes = await ReadEntryAsync(archive, SettingsArchiveFormat.ManifestEntryName, cancellationToken);
            if (manifestBytes is null)
                return SettingsArchivePreflightResult.NotSettingsArchive();

            SettingsArchiveManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<SettingsArchiveManifest>(manifestBytes, JsonOptions);
            }
            catch (JsonException)
            {
                return SettingsArchivePreflightResult.NotSettingsArchive();
            }

            if (manifest is null || !string.Equals(manifest.FormatIdentifier, SettingsArchiveFormat.Identifier, StringComparison.Ordinal))
                return SettingsArchivePreflightResult.NotSettingsArchive();

            var errors = new List<string>();
            if (!string.Equals(manifest.FormatVersion, SettingsArchiveFormat.CurrentVersion, StringComparison.Ordinal))
                errors.Add($"Unsupported settings archive format version '{manifest.FormatVersion}'.");
            if (manifest.DocumentVersion != SettingsArchiveFormat.CurrentDocumentVersion)
                errors.Add($"Unsupported settings document version '{manifest.DocumentVersion}'.");
            if (!string.Equals(manifest.Scope, "settings", StringComparison.Ordinal))
                errors.Add("Settings archive scope must be 'settings'.");

            var documentBytes = await ReadEntryAsync(archive, SettingsArchiveFormat.DocumentEntryName, cancellationToken);
            if (documentBytes is null)
                errors.Add("Settings archive is missing settings.json.");
            else if (!string.Equals(Convert.ToHexString(SHA256.HashData(documentBytes)).ToLowerInvariant(), manifest.SettingsSha256, StringComparison.Ordinal))
                errors.Add("Settings archive checksum does not match the manifest.");

            SettingsDocument? document = null;
            if (documentBytes is not null)
            {
                try
                {
                    document = JsonSerializer.Deserialize<SettingsDocument>(documentBytes, JsonOptions);
                }
                catch (JsonException)
                {
                    errors.Add("settings.json could not be parsed.");
                }
            }

            if (document is null)
                errors.Add("Settings archive does not contain a valid settings document.");
            else
            {
                try
                {
                    errors.AddRange(ValidateDocument(document));
                }
                catch (Exception) when (document.Providers is null
                    || document.Profiles is null
                    || document.ProfileProviders is null
                    || document.DownstreamIntegrations is null)
                {
                    errors.Add("Settings archive contains an invalid null entity section.");
                }
            }

            if (manifest.EncryptionKeyId is not null)
            {
                var fingerprint = encryption.GetKeyFingerprint(manifest.EncryptionKeyId);
                if (fingerprint is null)
                    errors.Add($"Required encryption key '{manifest.EncryptionKeyId}' is not present in the current key ring.");
                else if (!string.Equals(fingerprint, manifest.EncryptionKeyFingerprint, StringComparison.Ordinal))
                    errors.Add($"Encryption key '{manifest.EncryptionKeyId}' is present but its key material does not match this settings archive.");
            }

            if (document is not null && errors.Count == 0)
                errors.AddRange(ValidateEncryptedValues(document, manifest));

            return errors.Count == 0 && document is not null
                ? SettingsArchivePreflightResult.Succeeded(manifest, document)
                : SettingsArchivePreflightResult.Failed(errors, manifest);
        }
        catch (InvalidDataException)
        {
            return SettingsArchivePreflightResult.NotSettingsArchive();
        }
        catch (IOException ex)
        {
            return SettingsArchivePreflightResult.Failed([ex.Message]);
        }
    }

    public async Task<SettingsImportResult> ApplyAsync(string archivePath, CancellationToken cancellationToken)
    {
        var preflight = await PreflightAsync(archivePath, cancellationToken);
        if (!preflight.IsSettingsArchive)
            return SettingsImportResult.Failed("Archive is not a settings archive.");
        if (!preflight.Success)
            return new SettingsImportResult(false, preflight.Errors, new Dictionary<string, int>());

        var document = preflight.Document!;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (await db.Providers.AnyAsync(cancellationToken)
                || await db.Profiles.AnyAsync(cancellationToken)
                || await db.ProfileProviders.AnyAsync(cancellationToken)
                || await db.DownstreamIntegrations.AnyAsync(cancellationToken))
            {
                return SettingsImportResult.Failed("Settings import requires a clean target with no providers, profiles, profile-provider links, or downstream integrations.");
            }

            var siteSettings = await db.SiteSettings.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
            if (siteSettings is null)
                return SettingsImportResult.Failed("Target database does not contain the required SiteSettings singleton.");

            ApplySiteSettings(siteSettings, document.SiteSettings);
            var now = DateTime.UtcNow;
            var providerIds = document.Providers.ToDictionary(x => x.SourceId, _ => Guid.NewGuid().ToString("N"), StringComparer.Ordinal);
            var profileIds = document.Profiles.ToDictionary(x => x.SourceId, _ => Guid.NewGuid().ToString("N"), StringComparer.Ordinal);

            db.Providers.AddRange(document.Providers.Select(x => new Provider
            {
                ProviderId = providerIds[x.SourceId], Name = x.Name, Enabled = x.Enabled, PlaylistUrl = x.PlaylistUrl,
                XmltvUrl = x.XmltvUrl, HeadersJson = x.HeadersJson, UserAgent = x.UserAgent, TimeoutSeconds = x.TimeoutSeconds,
                MaxConcurrentStreams = x.MaxConcurrentStreams, IncludeVod = x.IncludeVod, IncludeSeries = x.IncludeSeries,
                ForceMpegTs = x.ForceMpegTs, CleanRelayMode = x.CleanRelayMode, XtreamBaseUrl = x.XtreamBaseUrl,
                XtreamUsername = x.XtreamUsername, XtreamEncryptedPassword = x.XtreamEncryptedPassword,
                XtreamIncludeXmltv = x.XtreamIncludeXmltv, CreatedUtc = now, UpdatedUtc = now,
            }));
            db.Profiles.AddRange(document.Profiles.Select(x => new Profile
            {
                ProfileId = profileIds[x.SourceId], Name = x.Name, Enabled = x.Enabled, IsActive = x.IsActive,
                OutputName = x.OutputName, MergeMode = x.MergeMode, RefreshScheduleKindOverride = x.RefreshScheduleKindOverride,
                RefreshStartupCatchupOverride = x.RefreshStartupCatchupOverride, CreatedUtc = now, UpdatedUtc = now,
            }));
            db.ProfileProviders.AddRange(document.ProfileProviders.Select(x => new ProfileProvider
            {
                ProfileId = profileIds[x.ProfileSourceId], ProviderId = providerIds[x.ProviderSourceId], Priority = x.Priority, Enabled = x.Enabled,
            }));
            db.DownstreamIntegrations.AddRange(document.DownstreamIntegrations.Select(x => new DownstreamIntegration
            {
                DownstreamIntegrationId = Guid.NewGuid().ToString("N"), ProfileId = x.ProfileSourceId is null ? null : profileIds[x.ProfileSourceId],
                Name = x.Name, Kind = x.Kind, BaseUrl = x.BaseUrl, ApiKeyEncrypted = x.ApiKeyEncrypted,
                WebhookHeadersJson = x.WebhookHeadersJson, TriggerOnLineupUpdate = x.TriggerOnLineupUpdate,
                TriggerOnGuideUpdate = x.TriggerOnGuideUpdate, Enabled = x.Enabled, CreatedUtc = now, UpdatedUtc = now,
            }));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return SettingsImportResult.Succeeded(new Dictionary<string, int>
            {
                ["SiteSettings"] = 1,
                ["Providers"] = document.Providers.Count,
                ["Profiles"] = document.Profiles.Count,
                ["ProfileProviders"] = document.ProfileProviders.Count,
                ["DownstreamIntegrations"] = document.DownstreamIntegrations.Count,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            logger.LogWarning(ex, "Settings archive import failed.");
            return SettingsImportResult.Failed("Settings import failed without applying changes.");
        }
    }

    private async Task<SettingsDocument> CreateDocumentAsync(CancellationToken cancellationToken)
    {
        var siteSettings = await db.SiteSettings.AsNoTracking().SingleAsync(x => x.Id == 1, cancellationToken);
        var providers = await db.Providers.AsNoTracking().OrderBy(x => x.ProviderId).ToListAsync(cancellationToken);
        var profiles = await db.Profiles.AsNoTracking().OrderBy(x => x.ProfileId).ToListAsync(cancellationToken);
        var links = await db.ProfileProviders.AsNoTracking().OrderBy(x => x.ProfileId).ThenBy(x => x.ProviderId).ToListAsync(cancellationToken);
        var integrations = await db.DownstreamIntegrations.AsNoTracking().OrderBy(x => x.DownstreamIntegrationId).ToListAsync(cancellationToken);

        return new SettingsDocument
        {
            SiteSettings = ToSettingsSiteSettings(siteSettings),
            Providers = providers.Select(x => new SettingsProvider
            {
                SourceId = x.ProviderId, Name = x.Name, Enabled = x.Enabled, PlaylistUrl = x.PlaylistUrl, XmltvUrl = x.XmltvUrl,
                HeadersJson = x.HeadersJson, UserAgent = x.UserAgent, TimeoutSeconds = x.TimeoutSeconds, MaxConcurrentStreams = x.MaxConcurrentStreams,
                IncludeVod = x.IncludeVod, IncludeSeries = x.IncludeSeries, ForceMpegTs = x.ForceMpegTs, CleanRelayMode = x.CleanRelayMode,
                XtreamBaseUrl = x.XtreamBaseUrl, XtreamUsername = x.XtreamUsername,
                XtreamEncryptedPassword = RewrapSecret(x.XtreamEncryptedPassword), XtreamIncludeXmltv = x.XtreamIncludeXmltv,
            }).ToList(),
            Profiles = profiles.Select(x => new SettingsProfile
            {
                SourceId = x.ProfileId, Name = x.Name, Enabled = x.Enabled, IsActive = x.IsActive, OutputName = x.OutputName,
                MergeMode = x.MergeMode, RefreshScheduleKindOverride = x.RefreshScheduleKindOverride,
                RefreshStartupCatchupOverride = x.RefreshStartupCatchupOverride,
            }).ToList(),
            ProfileProviders = links.Select(x => new SettingsProfileProvider
            {
                ProfileSourceId = x.ProfileId, ProviderSourceId = x.ProviderId, Priority = x.Priority, Enabled = x.Enabled,
            }).ToList(),
            DownstreamIntegrations = integrations.Select(x => new SettingsDownstreamIntegration
            {
                SourceId = x.DownstreamIntegrationId, ProfileSourceId = x.ProfileId, Name = x.Name, Kind = x.Kind, BaseUrl = x.BaseUrl,
                ApiKeyEncrypted = RewrapSecret(x.ApiKeyEncrypted), WebhookHeadersJson = x.WebhookHeadersJson,
                TriggerOnLineupUpdate = x.TriggerOnLineupUpdate, TriggerOnGuideUpdate = x.TriggerOnGuideUpdate, Enabled = x.Enabled,
            }).ToList(),
        };
    }

    private string? RewrapSecret(string? encryptedValue)
    {
        if (encryptedValue is null)
            return null;
        return encryption.Encrypt(encryption.Decrypt(encryptedValue));
    }

    private IReadOnlyList<string> ValidateEncryptedValues(SettingsDocument document, SettingsArchiveManifest manifest)
    {
        var encryptedValues = document.Providers.Select(x => x.XtreamEncryptedPassword)
            .Concat(document.DownstreamIntegrations.Select(x => x.ApiKeyEncrypted))
            .Where(x => x is not null)
            .Cast<string>()
            .ToList();
        if (encryptedValues.Count == 0)
            return [];
        if (manifest.EncryptionKeyId is null || manifest.EncryptionKeyFingerprint is null)
            return ["Settings archive contains encrypted values but does not identify its encryption key."];

        foreach (var value in encryptedValues)
        {
            try
            {
                _ = encryption.Decrypt(value);
            }
            catch (Exception)
            {
                return ["Settings archive contains an encrypted value that cannot be decrypted with the configured key ring."];
            }
        }

        return [];
    }

    private static IReadOnlyList<string> ValidateDocument(SettingsDocument document)
    {
        var errors = new List<string>();
        if (document.DocumentVersion != SettingsArchiveFormat.CurrentDocumentVersion)
            errors.Add($"Unsupported settings document version '{document.DocumentVersion}'.");
        if (document.SiteSettings is null)
            errors.Add("Settings document is missing SiteSettings.");
        ValidateIds(document.Providers.Select(x => x.SourceId), "provider", errors);
        ValidateIds(document.Profiles.Select(x => x.SourceId), "profile", errors);
        ValidateIds(document.DownstreamIntegrations.Select(x => x.SourceId), "downstream integration", errors);
        ValidateUniqueNames(document.Providers.Select(x => x.Name), "provider", errors);
        ValidateUniqueNames(document.Profiles.Select(x => x.Name), "profile", errors);

        var providerIds = document.Providers.Select(x => x.SourceId).ToHashSet(StringComparer.Ordinal);
        var profileIds = document.Profiles.Select(x => x.SourceId).ToHashSet(StringComparer.Ordinal);
        foreach (var link in document.ProfileProviders)
        {
            if (!profileIds.Contains(link.ProfileSourceId) || !providerIds.Contains(link.ProviderSourceId))
                errors.Add("A profile-provider link references a provider or profile outside this settings archive.");
        }
        if (document.ProfileProviders
            .GroupBy(x => (x.ProfileSourceId ?? string.Empty, x.ProviderSourceId ?? string.Empty))
            .Any(x => x.Count() > 1))
        {
            errors.Add("Settings document contains duplicate profile-provider links.");
        }
        foreach (var integration in document.DownstreamIntegrations)
        {
            if (integration.ProfileSourceId is not null && !profileIds.Contains(integration.ProfileSourceId))
                errors.Add("A downstream integration references a profile outside this settings archive.");
        }
        if (document.Profiles.Count(x => x.IsActive) > 1)
            errors.Add("Settings document contains more than one active profile.");
        return errors;
    }

    private static void ValidateIds(IEnumerable<string> values, string entityName, List<string> errors)
    {
        var ids = values.ToList();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            errors.Add($"Settings document contains blank or duplicate {entityName} source IDs.");
    }

    private static void ValidateUniqueNames(IEnumerable<string> values, string entityName, List<string> errors)
    {
        var names = values.ToList();
        if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            errors.Add($"Settings document contains blank or duplicate {entityName} names.");
    }

    private static async Task<byte[]?> ReadEntryAsync(ZipArchive archive, string entryName, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null || entry.Length > PortableBackupFormat.MaxMetadataEntrySizeBytes)
            return null;

        await using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var contents = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(contents, cancellationToken)) > 0)
        {
            total += read;
            if (total > PortableBackupFormat.MaxMetadataEntrySizeBytes)
                return null;
            await buffer.WriteAsync(contents.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string entryName, byte[] contents)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(contents);
    }

    private static SettingsSiteSettings ToSettingsSiteSettings(SiteSettings source) => new()
    {
        StreamingEnabled = source.StreamingEnabled, StreamMaxConcurrentSessions = source.StreamMaxConcurrentSessions,
        StreamIdleGraceSeconds = source.StreamIdleGraceSeconds, StreamIdleGraceHardCapSeconds = source.StreamIdleGraceHardCapSeconds,
        StreamBufferMaxBytesPerSession = source.StreamBufferMaxBytesPerSession, StreamBufferMaxBytesHardCap = source.StreamBufferMaxBytesHardCap,
        StreamBufferReadChunkSizeBytes = source.StreamBufferReadChunkSizeBytes,
        StreamReconnectReadStallTimeoutSeconds = source.StreamReconnectReadStallTimeoutSeconds,
        StreamReconnectOutageWindowSeconds = source.StreamReconnectOutageWindowSeconds,
        StreamReconnectConnectTimeoutSeconds = source.StreamReconnectConnectTimeoutSeconds, HdhrEnabled = source.HdhrEnabled,
        HdhrTunerCountOverride = source.HdhrTunerCountOverride, HdhrAdvertisedBaseUrl = source.HdhrAdvertisedBaseUrl,
        HdhrDiscoveryEnabled = source.HdhrDiscoveryEnabled, HdhrSsdpEnabled = source.HdhrSsdpEnabled,
        HdhrSiliconDustDiscoveryEnabled = source.HdhrSiliconDustDiscoveryEnabled, HdhrFriendlyName = source.HdhrFriendlyName,
        HdhrAllowedNetworks = source.HdhrAllowedNetworks, GeneratedHlsEnabled = source.GeneratedHlsEnabled,
        GeneratedHlsFfmpegPath = source.GeneratedHlsFfmpegPath, RefreshScheduleKind = source.RefreshScheduleKind,
        RefreshStartupCatchup = source.RefreshStartupCatchup, EventRetentionDays = source.EventRetentionDays,
        ObservabilityMetricsEnabled = source.ObservabilityMetricsEnabled, ObservabilityMetricsMode = source.ObservabilityMetricsMode,
        ObservabilityMetricsEnableChannelLabels = source.ObservabilityMetricsEnableChannelLabels,
        ObservabilityMetricsLocalAllowedCidrs = source.ObservabilityMetricsLocalAllowedCidrs,
        XtreamCompatibilityEnabled = source.XtreamCompatibilityEnabled,
    };

    private static void ApplySiteSettings(SiteSettings target, SettingsSiteSettings source)
    {
        target.StreamingEnabled = source.StreamingEnabled; target.StreamMaxConcurrentSessions = source.StreamMaxConcurrentSessions;
        target.StreamIdleGraceSeconds = source.StreamIdleGraceSeconds; target.StreamIdleGraceHardCapSeconds = source.StreamIdleGraceHardCapSeconds;
        target.StreamBufferMaxBytesPerSession = source.StreamBufferMaxBytesPerSession; target.StreamBufferMaxBytesHardCap = source.StreamBufferMaxBytesHardCap;
        target.StreamBufferReadChunkSizeBytes = source.StreamBufferReadChunkSizeBytes;
        target.StreamReconnectReadStallTimeoutSeconds = source.StreamReconnectReadStallTimeoutSeconds;
        target.StreamReconnectOutageWindowSeconds = source.StreamReconnectOutageWindowSeconds;
        target.StreamReconnectConnectTimeoutSeconds = source.StreamReconnectConnectTimeoutSeconds; target.HdhrEnabled = source.HdhrEnabled;
        target.HdhrTunerCountOverride = source.HdhrTunerCountOverride; target.HdhrAdvertisedBaseUrl = source.HdhrAdvertisedBaseUrl;
        target.HdhrDiscoveryEnabled = source.HdhrDiscoveryEnabled; target.HdhrSsdpEnabled = source.HdhrSsdpEnabled;
        target.HdhrSiliconDustDiscoveryEnabled = source.HdhrSiliconDustDiscoveryEnabled; target.HdhrFriendlyName = source.HdhrFriendlyName;
        target.HdhrAllowedNetworks = source.HdhrAllowedNetworks; target.GeneratedHlsEnabled = source.GeneratedHlsEnabled;
        target.GeneratedHlsFfmpegPath = source.GeneratedHlsFfmpegPath; target.RefreshScheduleKind = source.RefreshScheduleKind;
        target.RefreshStartupCatchup = source.RefreshStartupCatchup; target.EventRetentionDays = source.EventRetentionDays;
        target.ObservabilityMetricsEnabled = source.ObservabilityMetricsEnabled; target.ObservabilityMetricsMode = source.ObservabilityMetricsMode;
        target.ObservabilityMetricsEnableChannelLabels = source.ObservabilityMetricsEnableChannelLabels;
        target.ObservabilityMetricsLocalAllowedCidrs = source.ObservabilityMetricsLocalAllowedCidrs;
        target.XtreamCompatibilityEnabled = source.XtreamCompatibilityEnabled;
    }

    private static void SetOwnerOnlyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
