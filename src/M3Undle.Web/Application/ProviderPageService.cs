using M3Undle.Core.M3u;
using M3Undle.Web.Contracts.Providers;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace M3Undle.Web.Application;

public sealed class ProviderPageService(
    IServiceScopeFactory scopeFactory,
    ProviderFetcher fetcher,
    ConfigYamlService configService,
    EnvironmentVariableService envVarService,
    SecretEncryptionService encryption,
    IRefreshTrigger refreshTrigger,
    AppEventBus eventBus,
    ILogger<ProviderPageService> logger)
{
    public async Task<(List<ProfileListItemDto> Profiles, List<ProviderDto> Providers)> GetPageDataAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profiles = await db.Profiles
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new ProfileListItemDto
            {
                ProfileId = x.ProfileId,
                Name = x.Name,
                OutputName = x.OutputName,
                MergeMode = x.MergeMode,
                Enabled = x.Enabled,
            })
            .ToListAsync(cancellationToken);

        var providers = await db.Providers
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var dtos = await BuildProviderDtosAsync(db, providers, cancellationToken);
        return (profiles, dtos);
    }

    public Task<bool> GetEncryptionAvailableAsync() => Task.FromResult(encryption.IsAvailable);

    public async Task<(FileBrowseDto? Data, string? Error)> BrowseFilesystemAsync(string? path)
    {
        var rootDir = envVarService.GetValue("M3UNDLE_M3U_DIR");
        if (string.IsNullOrWhiteSpace(rootDir))
            return (null, "M3UNDLE_M3U_DIR is not set. Set this environment variable to the directory containing your .m3u files and restart M3Undle.");

        var targetPath = string.IsNullOrWhiteSpace(path)
            ? rootDir
            : Path.GetFullPath(path);

        var normalizedRoot = Path.GetFullPath(rootDir);
        if (!targetPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return (null, $"Path is outside the allowed directory ({normalizedRoot}).");

        if (!Directory.Exists(targetPath))
            return (null, $"Directory not found: {targetPath}");

        string? parentPath = null;
        if (!string.Equals(targetPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(targetPath)?.FullName;
            if (parent is not null && parent.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                parentPath = parent;
        }

        var entries = new List<FileBrowseEntryDto>();
        try
        {
            foreach (var dir in Directory.GetDirectories(targetPath).OrderBy(x => x))
            {
                entries.Add(new FileBrowseEntryDto
                {
                    Name = Path.GetFileName(dir),
                    Path = dir,
                    IsDirectory = true,
                });
            }

            foreach (var file in Directory.GetFiles(targetPath).OrderBy(x => x))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".m3u" or ".m3u8" or ".txt")
                {
                    entries.Add(new FileBrowseEntryDto
                    {
                        Name = Path.GetFileName(file),
                        Path = file,
                        IsDirectory = false,
                    });
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return (null, $"Access denied: {ex.Message}");
        }

        return (new FileBrowseDto
        {
            CurrentPath = targetPath,
            ParentPath = parentPath,
            Entries = entries,
        }, null);
    }

    public async Task<List<ConfigYamlProviderDto>> GetAvailableConfigProvidersAsync(CancellationToken cancellationToken)
    {
        var configProviders = await configService.LoadProvidersAsync();
        return configProviders.Select(p => new ConfigYamlProviderDto
        {
            Name = p.Name,
            PlaylistUrl = p.PlaylistUrl,
            XmltvUrl = p.XmltvUrl,
            RequiresEnvVars = envVarService.RequiresSubstitution(p.PlaylistUrl),
            MissingEnvVars = envVarService.ValidateVariables(p.PlaylistUrl).Missing.ToList(),
        }).ToList();
    }

    public async Task<(ProbeConfigProviderResultDto? Result, string? Error)> ProbeConfigProviderAsync(string name, CancellationToken cancellationToken)
    {
        var configProviders = await configService.LoadProvidersAsync();
        var configProvider = configProviders.FirstOrDefault(p => p.Name == name);
        if (configProvider is null)
            return (null, $"Provider '{name}' not found in config.yaml");

        var (isValid, missing) = envVarService.ValidateVariables(configProvider.PlaylistUrl);
        if (!isValid)
            return (null, $"Missing environment variables: {string.Join(", ", missing)}");

        var probe = new Provider
        {
            ProviderId = "(probe)",
            Name = configProvider.Name,
            PlaylistUrl = configProvider.PlaylistUrl,
            XmltvUrl = configProvider.XmltvUrl,
            HeadersJson = configProvider.Headers is not null ? JsonSerializer.Serialize(configProvider.Headers) : null,
            UserAgent = configProvider.UserAgent,
            TimeoutSeconds = configProvider.TimeoutSeconds,
            Enabled = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };

        try
        {
            var result = await fetcher.FetchPlaylistAsync(probe, cancellationToken);
            return (new ProbeConfigProviderResultDto { Ok = true, ChannelCount = result.Channels.Count }, null);
        }
        catch (Exception ex) when (ex is ProviderFetchException or ProviderParseException)
        {
            return (new ProbeConfigProviderResultDto { Ok = false, Error = ex.Message }, null);
        }
    }

    public async Task<(ProviderDto? Provider, string? Error)> ImportConfigProviderAsync(
        ImportConfigProviderRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var configProviders = await configService.LoadProvidersAsync();
        var configProvider = configProviders.FirstOrDefault(p => p.Name == request.Name);
        if (configProvider is null)
            return (null, $"Provider '{request.Name}' not found in config.yaml");

        var (isValid, missing) = envVarService.ValidateVariables(configProvider.PlaylistUrl);
        if (!isValid)
            return (null, $"Missing environment variables: {string.Join(", ", missing)}");

        var existing = await db.Providers.AsNoTracking().FirstOrDefaultAsync(p => p.Name == configProvider.Name, cancellationToken);
        if (existing is not null)
            return (null, $"Provider '{configProvider.Name}' already exists");

        var now = DateTime.UtcNow;
        var provider = new Provider
        {
            ProviderId = Guid.NewGuid().ToString(),
            Name = configProvider.Name,
            PlaylistUrl = configProvider.PlaylistUrl,
            XmltvUrl = configProvider.XmltvUrl,
            HeadersJson = configProvider.Headers is not null ? JsonSerializer.Serialize(configProvider.Headers) : null,
            UserAgent = configProvider.UserAgent,
            TimeoutSeconds = configProvider.TimeoutSeconds,
            Enabled = configProvider.Enabled,
            IsActive = false,
            IncludeVod = request.IncludeVod,
            IncludeSeries = request.IncludeSeries,
            MaxConcurrentStreams = request.MaxConcurrentStreams is > 0 ? request.MaxConcurrentStreams : null,
            ConfigSourcePath = configProvider.SourcePath,
            NeedsEnvVarSubstitution = envVarService.RequiresSubstitution(configProvider.PlaylistUrl),
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        db.Providers.Add(provider);
        var profileName = await GetUniqueProfileNameAsync(db, configProvider.Name, cancellationToken);
        var profile = new Profile
        {
            ProfileId = Guid.NewGuid().ToString(),
            Name = profileName,
            OutputName = "m3undle",
            MergeMode = "replace",
            Enabled = true,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.Profiles.Add(profile);
        ApplyProviderProfiles(db, provider.ProviderId, [profile.ProfileId]);

        await db.SaveChangesAsync(cancellationToken);
        var dto = (await BuildProviderDtosAsync(db, [provider], cancellationToken)).Single();
        eventBus.Publish(AppEventKind.ProviderChanged);
        return (dto, null);
    }

    public async Task<(ProviderDto? Provider, string? Error)> CreateProviderAsync(CreateProviderRequest request, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var isXtream = !string.IsNullOrWhiteSpace(request.XtreamBaseUrl);
        var validationErrors = isXtream
            ? await ValidateXtreamProviderRequestAsync(db, request.Name, request.XtreamBaseUrl!, request.XtreamUsername, request.XtreamPassword, request.AssociateToProfileIds, null, cancellationToken)
            : await ValidateProviderRequestAsync(db, request.Name, request.PlaylistUrl, request.XmltvUrl, request.HeadersJson, request.TimeoutSeconds, request.AssociateToProfileIds, null, cancellationToken);

        if (validationErrors.Count > 0)
            return (null, FirstError(validationErrors));

        var now = DateTime.UtcNow;
        var provider = new Provider
        {
            ProviderId = Guid.NewGuid().ToString(),
            Name = request.Name.Trim(),
            Enabled = request.Enabled,
            PlaylistUrl = isXtream ? string.Empty : request.PlaylistUrl!.Trim(),
            XmltvUrl = isXtream ? null : (string.IsNullOrWhiteSpace(request.XmltvUrl) ? null : request.XmltvUrl.Trim()),
            HeadersJson = isXtream ? null : (string.IsNullOrWhiteSpace(request.HeadersJson) ? null : request.HeadersJson),
            UserAgent = isXtream ? null : (string.IsNullOrWhiteSpace(request.UserAgent) ? null : request.UserAgent.Trim()),
            TimeoutSeconds = request.TimeoutSeconds,
            MaxConcurrentStreams = request.MaxConcurrentStreams is > 0 ? request.MaxConcurrentStreams : null,
            IncludeVod = request.IncludeVod,
            IncludeSeries = request.IncludeSeries,
            XtreamBaseUrl = isXtream ? request.XtreamBaseUrl!.TrimEnd('/') : null,
            XtreamUsername = isXtream ? request.XtreamUsername?.Trim() : null,
            XtreamEncryptedPassword = isXtream ? encryption.Encrypt(request.XtreamPassword!) : null,
            XtreamIncludeXmltv = isXtream && request.XtreamIncludeXmltv,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        db.Providers.Add(provider);

        var profileIdsToApply = request.AssociateToProfileIds
            ?.Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (profileIdsToApply is null or { Count: 0 })
        {
            var profileName = await GetUniqueProfileNameAsync(db, request.Name.Trim(), cancellationToken);
            var profile = new Profile
            {
                ProfileId = Guid.NewGuid().ToString(),
                Name = profileName,
                OutputName = "m3undle",
                MergeMode = "replace",
                Enabled = true,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            db.Profiles.Add(profile);
            profileIdsToApply = [profile.ProfileId];
        }

        ApplyProviderProfiles(db, provider.ProviderId, profileIdsToApply);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return (null, "Provider could not be created due to a database conflict.");
        }

        var dto = (await BuildProviderDtosAsync(db, [provider], cancellationToken)).Single();
        eventBus.Publish(AppEventKind.ProviderChanged);
        return (dto, null);
    }

    public async Task<(ProfileListItemDto? Profile, string? Error)> CreateProfileAsync(string name, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (string.IsNullOrWhiteSpace(name))
            return (null, "name is required.");

        var trimmed = name.Trim();
        var duplicate = await db.Profiles.AsNoTracking().AnyAsync(x => x.Name == trimmed, cancellationToken);
        if (duplicate)
            return (null, $"Profile '{trimmed}' already exists.");

        var now = DateTime.UtcNow;
        var profile = new Profile
        {
            ProfileId = Guid.NewGuid().ToString(),
            Name = trimmed,
            OutputName = "m3undle",
            MergeMode = "replace",
            Enabled = true,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        db.Profiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);

        return (new ProfileListItemDto
        {
            ProfileId = profile.ProfileId,
            Name = profile.Name,
            OutputName = profile.OutputName,
            MergeMode = profile.MergeMode,
            Enabled = profile.Enabled,
        }, null);
    }

    public async Task<string?> UpdateProviderAsync(string providerId, UpdateProviderRequest request, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var provider = await db.Providers.SingleOrDefaultAsync(x => x.ProviderId == providerId, cancellationToken);
        if (provider is null)
            return "Provider not found.";

        var isXtream = provider.XtreamBaseUrl is not null;
        Dictionary<string, string[]> validationErrors;
        if (isXtream)
        {
            validationErrors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name))
                validationErrors["name"] = ["name is required."];
            if (!string.IsNullOrWhiteSpace(request.XtreamBaseUrl) && !IsValidHttpUrl(request.XtreamBaseUrl))
                validationErrors["xtreamBaseUrl"] = ["xtreamBaseUrl must be an http/https URL."];
            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                var dup = await db.Providers.AsNoTracking()
                    .AnyAsync(x => x.Name == request.Name.Trim() && x.ProviderId != providerId, cancellationToken);
                if (dup) validationErrors["name"] = ["name must be unique."];
            }
        }
        else
        {
            validationErrors = await ValidateProviderRequestAsync(db, request.Name, request.PlaylistUrl, request.XmltvUrl, request.HeadersJson, request.TimeoutSeconds, request.AssociateToProfileIds, providerId, cancellationToken);
        }

        if (validationErrors.Count > 0)
            return FirstError(validationErrors);

        provider.Name = request.Name.Trim();
        provider.Enabled = request.Enabled;
        provider.TimeoutSeconds = request.TimeoutSeconds;
        provider.MaxConcurrentStreams = request.MaxConcurrentStreams is > 0 ? request.MaxConcurrentStreams : null;
        provider.IncludeVod = request.IncludeVod;
        provider.IncludeSeries = request.IncludeSeries;
        provider.UpdatedUtc = DateTime.UtcNow;

        if (isXtream)
        {
            if (!string.IsNullOrWhiteSpace(request.XtreamBaseUrl))
                provider.XtreamBaseUrl = request.XtreamBaseUrl.TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(request.XtreamUsername))
                provider.XtreamUsername = request.XtreamUsername.Trim();
            provider.XtreamIncludeXmltv = request.XtreamIncludeXmltv;
        }
        else
        {
            provider.PlaylistUrl = request.PlaylistUrl!.Trim();
            provider.XmltvUrl = string.IsNullOrWhiteSpace(request.XmltvUrl) ? null : request.XmltvUrl.Trim();
            provider.HeadersJson = string.IsNullOrWhiteSpace(request.HeadersJson) ? null : request.HeadersJson;
            provider.UserAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? null : request.UserAgent.Trim();
        }

        var existingLinks = await db.ProfileProviders.Where(x => x.ProviderId == providerId).ToListAsync(cancellationToken);
        db.ProfileProviders.RemoveRange(existingLinks);
        ApplyProviderProfiles(db, providerId, request.AssociateToProfileIds);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return "Provider could not be updated due to a database conflict.";
        }

        eventBus.Publish(AppEventKind.ProviderChanged);
        return null;
    }

    public async Task<string?> UpdateXtreamPasswordAsync(string providerId, string password, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var provider = await db.Providers.SingleOrDefaultAsync(x => x.ProviderId == providerId, cancellationToken);
        if (provider is null)
            return "Provider not found.";

        if (string.IsNullOrWhiteSpace(password))
            return "Password is required.";

        if (!encryption.IsAvailable)
            return "M3UNDLE_ENCRYPTION_KEY is not configured. Set this environment variable to a Base64-encoded 32-byte value before storing passwords.";

        provider.XtreamEncryptedPassword = encryption.Encrypt(password);
        provider.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public async Task<string?> DeleteProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        if (refreshTrigger.IsRefreshing)
            return "A snapshot refresh is currently in progress. Please wait for it to finish before deleting a provider.";

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var provider = await db.Providers.SingleOrDefaultAsync(x => x.ProviderId == providerId, cancellationToken);
        if (provider is null)
            return "Provider not found.";

        var channelSources = await db.ChannelSources.Where(x => x.ProviderId == providerId).ToListAsync(cancellationToken);
        db.ChannelSources.RemoveRange(channelSources);

        var providerChannels = await db.ProviderChannels.Where(x => x.ProviderId == providerId).ToListAsync(cancellationToken);
        db.ProviderChannels.RemoveRange(providerChannels);

        var providerGroups = await db.ProviderGroups.Where(x => x.ProviderId == providerId).ToListAsync(cancellationToken);
        db.ProviderGroups.RemoveRange(providerGroups);

        var fetchRuns = await db.FetchRuns.Where(x => x.ProviderId == providerId).ToListAsync(cancellationToken);
        db.FetchRuns.RemoveRange(fetchRuns);

        var profileProviders = await db.ProfileProviders.Where(x => x.ProviderId == providerId).ToListAsync(cancellationToken);
        db.ProfileProviders.RemoveRange(profileProviders);

        db.Providers.Remove(provider);
        await db.SaveChangesAsync(cancellationToken);

        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Provider" });
        logger.LogInformation("Provider deleted: {ProviderId} '{Name}'.", providerId, provider.Name);
        eventBus.Publish(AppEventKind.ProviderChanged);

        return null;
    }

    public async Task<string?> SetProviderEnabledAsync(string providerId, bool enabled, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var provider = await db.Providers.SingleOrDefaultAsync(x => x.ProviderId == providerId, cancellationToken);
        if (provider is null)
            return "Provider not found.";

        provider.Enabled = enabled;
        provider.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Provider" });
        logger.LogInformation("Provider {ProviderId} enabled={Enabled}.", providerId, provider.Enabled);
        eventBus.Publish(AppEventKind.ProviderChanged);

        return null;
    }

    public async Task<string?> SetProviderActiveAsync(string providerId, bool isActive, CancellationToken cancellationToken)
    {
        if (refreshTrigger.IsRefreshing)
            return "A snapshot refresh is currently in progress. Please wait for it to finish.";

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var exists = await db.Providers.AnyAsync(x => x.ProviderId == providerId, cancellationToken);
        if (!exists)
            return "Provider not found.";

        var now = DateTime.UtcNow;

        if (isActive)
        {
            await db.Providers
                .Where(x => x.IsActive && x.ProviderId != providerId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.IsActive, false)
                    .SetProperty(p => p.UpdatedUtc, now), cancellationToken);
        }

        await db.Providers
            .Where(x => x.ProviderId == providerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.IsActive, isActive)
                .SetProperty(p => p.UpdatedUtc, now), cancellationToken);

        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Provider" });
        logger.LogInformation("Provider {ProviderId} set active={IsActive}.", providerId, isActive);
        eventBus.Publish(AppEventKind.ProviderChanged);

        if (isActive)
        {
            eventBus.Publish(AppEventKind.ProviderActivated);
            refreshTrigger.TriggerRefresh();
        }

        return null;
    }

    public async Task<(ProviderPreviewDto? Preview, string? Error)> RefreshPreviewAsync(
        string providerId,
        RefreshPreviewRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var provider = await db.Providers.SingleOrDefaultAsync(x => x.ProviderId == providerId, cancellationToken);
        if (provider is null)
            return (null, "Provider not found.");

        var sampleSize = NormalizeSampleSize(request.SampleSize);
        if (sampleSize is null)
            return (null, "sampleSize must be between 1 and 50.");

        var now = DateTime.UtcNow;
        var fetchRun = new FetchRun
        {
            FetchRunId = Guid.NewGuid().ToString(),
            ProviderId = providerId,
            StartedUtc = now,
            Status = "running",
            Type = "preview",
        };

        db.FetchRuns.Add(fetchRun);
        await db.SaveChangesAsync(cancellationToken);

        PlaylistFetchResult fetchResult;
        try
        {
            fetchResult = await fetcher.FetchPlaylistAsync(provider, cancellationToken);
        }
        catch (ProviderFetchException ex)
        {
            fetchRun.FinishedUtc = DateTime.UtcNow;
            fetchRun.ErrorSummary = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            return (null, ex.Message);
        }
        catch (ProviderParseException ex)
        {
            fetchRun.FinishedUtc = DateTime.UtcNow;
            fetchRun.ErrorSummary = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            return (null, ex.Message);
        }

        fetchRun.FinishedUtc = DateTime.UtcNow;
        fetchRun.Status = "ok";
        fetchRun.ErrorSummary = null;
        fetchRun.ChannelCountSeen = fetchResult.Channels.Count;
        fetchRun.PlaylistBytes = (int)Math.Min(fetchResult.Bytes, int.MaxValue);

        await db.SaveChangesAsync(CancellationToken.None);

        var preview = BuildPreviewFromParsed(
            providerId,
            fetchRun.StartedUtc,
            fetchRun.FetchRunId,
            fetchResult.Channels,
            sampleSize.Value,
            request.GroupContains);

        return (preview, null);
    }

    private static string FirstError(Dictionary<string, string[]> errors)
    {
        foreach (var kv in errors)
        {
            var first = kv.Value.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
                return first;
        }

        return "Validation failed.";
    }

    private static int? NormalizeSampleSize(int? value)
    {
        if (value is null)
            return 10;

        return value is < 1 or > 50 ? null : value;
    }

    private static async Task<Dictionary<string, string[]>> ValidateProviderRequestAsync(
        ApplicationDbContext db,
        string name,
        string? playlistUrl,
        string? xmltvUrl,
        string? headersJson,
        int timeoutSeconds,
        List<string>? associateToProfileIds,
        string? providerId,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(name))
            errors["name"] = ["name is required."];

        if (string.IsNullOrWhiteSpace(playlistUrl) || (!IsValidHttpUrl(playlistUrl) && !IsValidFileUri(playlistUrl)))
            errors["playlistUrl"] = ["playlistUrl must be an http/https URL or a file:// URI."];
        else if (IsValidFileUri(playlistUrl) && !File.Exists(new Uri(playlistUrl.Trim()).LocalPath))
            errors["playlistUrl"] = ["Local file not found on the server."];

        if (!string.IsNullOrWhiteSpace(xmltvUrl) && !IsValidHttpUrl(xmltvUrl) && !IsValidFileUri(xmltvUrl))
            errors["xmltvUrl"] = ["xmltvUrl must be an http/https URL or a file:// URI when provided."];
        else if (!string.IsNullOrWhiteSpace(xmltvUrl) && IsValidFileUri(xmltvUrl) && !File.Exists(new Uri(xmltvUrl.Trim()).LocalPath))
            errors["xmltvUrl"] = ["Local XMLTV file not found on the server."];

        if (timeoutSeconds is < 1 or > 300)
            errors["timeoutSeconds"] = ["timeoutSeconds must be between 1 and 300."];

        if (!string.IsNullOrWhiteSpace(headersJson) && !TryValidateHeadersJson(headersJson, out var headersJsonError))
            errors["headersJson"] = [headersJsonError!];

        if (!string.IsNullOrWhiteSpace(name))
        {
            var duplicateName = await db.Providers
                .AsNoTracking()
                .AnyAsync(x => x.Name == name.Trim() && x.ProviderId != providerId, cancellationToken);

            if (duplicateName)
                errors["name"] = ["name must be unique."];
        }

        var profileIds = (associateToProfileIds ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (profileIds.Count > 0)
        {
            var existingCount = await db.Profiles
                .AsNoTracking()
                .CountAsync(x => profileIds.Contains(x.ProfileId), cancellationToken);

            if (existingCount != profileIds.Count)
                errors["associateToProfileIds"] = ["One or more profile ids do not exist."];
        }

        return errors;
    }

    private async Task<Dictionary<string, string[]>> ValidateXtreamProviderRequestAsync(
        ApplicationDbContext db,
        string name,
        string xtreamBaseUrl,
        string? xtreamUsername,
        string? xtreamPassword,
        List<string>? associateToProfileIds,
        string? providerId,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(name))
            errors["name"] = ["name is required."];

        if (string.IsNullOrWhiteSpace(xtreamBaseUrl) || !IsValidHttpUrl(xtreamBaseUrl))
            errors["xtreamBaseUrl"] = ["xtreamBaseUrl must be an http/https URL."];

        if (string.IsNullOrWhiteSpace(xtreamUsername))
            errors["xtreamUsername"] = ["xtreamUsername is required."];

        if (string.IsNullOrWhiteSpace(xtreamPassword))
            errors["xtreamPassword"] = ["xtreamPassword is required."];

        if (!encryption.IsAvailable)
            errors["encryption"] = ["M3UNDLE_ENCRYPTION_KEY is not configured. Set this environment variable to a Base64-encoded 32-byte value before storing passwords."];

        if (!string.IsNullOrWhiteSpace(name))
        {
            var dup = await db.Providers.AsNoTracking()
                .AnyAsync(x => x.Name == name.Trim() && x.ProviderId != providerId, cancellationToken);
            if (dup) errors["name"] = ["name must be unique."];
        }

        var profileIds = (associateToProfileIds ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (profileIds.Count > 0)
        {
            var existingCount = await db.Profiles
                .AsNoTracking()
                .CountAsync(x => profileIds.Contains(x.ProfileId), cancellationToken);

            if (existingCount != profileIds.Count)
                errors["associateToProfileIds"] = ["One or more profile ids do not exist."];
        }

        return errors;
    }

    private static async Task<string> GetUniqueProfileNameAsync(ApplicationDbContext db, string baseName, CancellationToken cancellationToken)
    {
        var name = baseName;
        var counter = 2;
        while (await db.Profiles.AsNoTracking().AnyAsync(p => p.Name == name, cancellationToken))
            name = $"{baseName} {counter++}";

        return name;
    }

    private static void ApplyProviderProfiles(ApplicationDbContext db, string providerId, List<string>? profileIdsInput)
    {
        var profileIds = (profileIdsInput ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        for (var i = 0; i < profileIds.Count; i++)
        {
            db.ProfileProviders.Add(new ProfileProvider
            {
                ProviderId = providerId,
                ProfileId = profileIds[i],
                Priority = i + 1,
                Enabled = true,
            });
        }
    }

    private static bool IsValidHttpUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    private static bool IsValidFileUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme == Uri.UriSchemeFile && !string.IsNullOrWhiteSpace(uri.LocalPath);
    }

    private static bool TryValidateHeadersJson(string value, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "headersJson must be a JSON object of string:string pairs.";
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    error = "headersJson values must be strings.";
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            error = "headersJson must be valid JSON.";
            return false;
        }
    }

    private static ProviderPreviewDto BuildPreviewFromParsed(
        string providerId,
        DateTime fetchStartedUtc,
        string fetchRunId,
        IReadOnlyList<ParsedProviderChannel> channels,
        int sampleSize,
        string? groupContains)
    {
        var normalizedGroupFilter = string.IsNullOrWhiteSpace(groupContains) ? null : groupContains.Trim();

        var grouped = channels
            .Select(x => (
                DisplayName: x.DisplayName,
                TvgId: x.TvgId,
                GroupName: string.IsNullOrWhiteSpace(x.GroupTitle) ? "(Ungrouped)" : x.GroupTitle,
                StreamUrl: x.StreamUrl))
            .Where(x => normalizedGroupFilter is null || x.GroupName.Contains(normalizedGroupFilter, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.GroupName, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

        var previewGroups = grouped
            .Select(group => new ProviderPreviewGroupDto
            {
                GroupName = group.Key,
                ChannelCount = group.Count(),
                SampleChannels = group
                    .OrderBy(x => x.DisplayName, StringComparer.Ordinal)
                    .Take(sampleSize)
                    .Select(x => new ProviderPreviewSampleChannelDto
                    {
                        ProviderChannelId = string.Empty,
                        DisplayName = x.DisplayName,
                        TvgId = x.TvgId,
                        HasStreamUrl = !string.IsNullOrWhiteSpace(x.StreamUrl),
                        StreamUrlRedacted = RedactStreamUrl(x.StreamUrl),
                    })
                    .ToList(),
            })
            .ToList();

        return new ProviderPreviewDto
        {
            ProviderId = providerId,
            PreviewGeneratedUtc = DateTime.UtcNow,
            Source = new ProviderPreviewSourceDto
            {
                Kind = "latest-successful-provider-refresh",
                FetchRunId = fetchRunId,
                FetchStartedUtc = fetchStartedUtc,
            },
            Totals = new ProviderPreviewTotalsDto
            {
                GroupCount = previewGroups.Count,
                ChannelCount = previewGroups.Sum(x => x.ChannelCount),
            },
            Groups = previewGroups,
        };
    }

    private static async Task<List<ProviderDto>> BuildProviderDtosAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<Provider> providers,
        CancellationToken cancellationToken)
    {
        if (providers.Count == 0)
            return [];

        var providerIds = providers.Select(x => x.ProviderId).ToList();
        var profileLinks = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => providerIds.Contains(x.ProviderId))
            .ToListAsync(cancellationToken);

        var latestRefreshByProvider = await db.FetchRuns
            .AsNoTracking()
            .Where(x => providerIds.Contains(x.ProviderId))
            .OrderByDescending(x => x.StartedUtc)
            .ToListAsync(cancellationToken);

        var latestRefreshLookup = latestRefreshByProvider
            .GroupBy(x => x.ProviderId)
            .ToDictionary(x => x.Key, x => x.First());

        var profileIds = profileLinks.Select(x => x.ProfileId).Distinct().ToList();
        var snapshotsByProfile = profileIds.Count == 0
            ? new Dictionary<string, Snapshot>()
            : (await db.Snapshots
                .AsNoTracking()
                .Where(x => profileIds.Contains(x.ProfileId))
                .OrderByDescending(x => x.CreatedUtc)
                .ToListAsync(cancellationToken))
                .GroupBy(x => x.ProfileId)
                .ToDictionary(x => x.Key, x => x.First());

        var linkLookup = profileLinks
            .GroupBy(x => x.ProviderId)
            .ToDictionary(
                x => x.Key,
                x => x.OrderBy(y => y.Priority).ThenBy(y => y.ProfileId).Select(y => y.ProfileId).ToList());

        return providers
            .OrderBy(x => x.Name)
            .Select(provider =>
            {
                linkLookup.TryGetValue(provider.ProviderId, out var associatedProfileIds);
                associatedProfileIds ??= [];

                latestRefreshLookup.TryGetValue(provider.ProviderId, out var latestRefresh);

                var latestSnapshots = associatedProfileIds
                    .Where(profileId => snapshotsByProfile.ContainsKey(profileId))
                    .Select(profileId => snapshotsByProfile[profileId])
                    .OrderByDescending(x => x.CreatedUtc)
                    .ThenBy(x => x.ProfileId)
                    .Select(x => new ProviderLatestSnapshotDto
                    {
                        SnapshotId = x.SnapshotId,
                        ProfileId = x.ProfileId,
                        Status = x.Status,
                        CreatedUtc = x.CreatedUtc,
                    })
                    .ToList();

                return new ProviderDto
                {
                    ProviderId = provider.ProviderId,
                    Name = provider.Name,
                    PlaylistUrl = provider.PlaylistUrl,
                    XmltvUrl = provider.XmltvUrl,
                    HeadersJson = provider.HeadersJson,
                    UserAgent = provider.UserAgent,
                    Enabled = provider.Enabled,
                    IsActive = provider.IsActive,
                    TimeoutSeconds = provider.TimeoutSeconds,
                    MaxConcurrentStreams = provider.MaxConcurrentStreams,
                    IncludeVod = provider.IncludeVod,
                    IncludeSeries = provider.IncludeSeries,
                    AssociatedProfileIds = associatedProfileIds,
                    XtreamBaseUrl = provider.XtreamBaseUrl,
                    XtreamUsername = provider.XtreamUsername,
                    XtreamIncludeXmltv = provider.XtreamIncludeXmltv,
                    LastRefresh = latestRefresh is null
                        ? null
                        : new ProviderLastRefreshDto
                        {
                            Status = latestRefresh.Status,
                            StartedUtc = latestRefresh.StartedUtc,
                            FinishedUtc = latestRefresh.FinishedUtc,
                            ErrorSummary = latestRefresh.ErrorSummary,
                            ChannelCountSeen = latestRefresh.ChannelCountSeen,
                        },
                    LatestSnapshots = latestSnapshots,
                };
            })
            .ToList();
    }

    private static string? RedactStreamUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return builder.Uri.GetLeftPart(UriPartial.Path);
    }
}
