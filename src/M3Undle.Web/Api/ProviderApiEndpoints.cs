using M3Undle.Web.Application;
using M3Undle.Web.Contracts.Providers;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace M3Undle.Web.Api;

public static class ProviderApiEndpoints
{
    private sealed class ProviderApiLog { }

    public static IEndpointRouteBuilder MapProviderApiEndpoints(this IEndpointRouteBuilder app)
    {
        var profiles = app.MapGroup("/api/v1/profiles");
        profiles.MapGet("/", ListProfilesAsync);
        profiles.MapPost("/", CreateProfileAsync);

        var providers = app.MapGroup("/api/v1/providers");
        providers.MapGet("/", ListProvidersAsync);
        providers.MapPost("/", CreateProviderAsync);
        providers.MapGet("/{providerId}", GetProviderAsync);
        providers.MapPut("/{providerId}", UpdateProviderAsync);
        providers.MapDelete("/{providerId}", DeleteProviderAsync);
        providers.MapPatch("/{providerId}/enabled", SetProviderEnabledAsync);
        providers.MapPatch("/{providerId}/active", SetProviderActiveAsync);
        providers.MapGet("/{providerId}/preview", GetPreviewAsync);
        providers.MapPost("/{providerId}/refresh-preview", RefreshPreviewAsync);
        providers.MapGet("/{providerId}/status", GetProviderStatusAsync);
        providers.MapGet("/config/available", GetAvailableConfigProvidersAsync);
        providers.MapPost("/config/import", ImportConfigProviderAsync);
        providers.MapPost("/config/probe", ProbeConfigProviderAsync);
        providers.MapGet("/{providerId}/health", GetProviderHealthAsync);

        var snapshots = app.MapGroup("/api/v1/snapshots");
        snapshots.MapPost("/refresh", TriggerRefreshAsync);
        snapshots.MapPost("/build", TriggerBuildOnlyAsync);

        return app;
    }

    // -------------------------------------------------------------------------
    // Profiles
    // -------------------------------------------------------------------------

    private static async Task<Ok<List<ProfileListItemDto>>> ListProfilesAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        try
        {
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

            return TypedResults.Ok(profiles);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TypedResults.Ok(new List<ProfileListItemDto>());
        }
    }

    private static async Task<Results<Created<ProfileListItemDto>, ValidationProblem, Conflict<string>>> CreateProfileAsync(
        CreateProfileRequest request,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = ["name is required."]
            });
        }

        var duplicate = await db.Profiles.AsNoTracking().AnyAsync(x => x.Name == request.Name.Trim(), cancellationToken);
        if (duplicate)
        {
            return TypedResults.Conflict($"Profile '{request.Name.Trim()}' already exists.");
        }

        var now = DateTime.UtcNow;
        var profile = new Profile
        {
            ProfileId = Guid.NewGuid().ToString(),
            Name = request.Name.Trim(),
            OutputName = "m3undle",
            MergeMode = "replace",
            Enabled = true,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        db.Profiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/v1/profiles/{profile.ProfileId}", new ProfileListItemDto
        {
            ProfileId = profile.ProfileId,
            Name = profile.Name,
            OutputName = profile.OutputName,
            MergeMode = profile.MergeMode,
            Enabled = profile.Enabled,
        });
    }

    // -------------------------------------------------------------------------
    // Providers
    // -------------------------------------------------------------------------

    private static async Task<Ok<List<ProviderDto>>> ListProvidersAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            var providers = await db.Providers
                .AsNoTracking()
                .OrderBy(x => x.Name)
                .ToListAsync(cancellationToken);

            var dtos = await BuildProviderDtosAsync(db, providers, cancellationToken);
            return TypedResults.Ok(dtos);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TypedResults.Ok(new List<ProviderDto>());
        }
    }

    private static async Task<Results<Created<ProviderDto>, ValidationProblem, Conflict<string>>> CreateProviderAsync(
        CreateProviderRequest request,
        ApplicationDbContext db,
        AppEventBus eventBus,
        ILogger<ProviderApiLog> logger,
        CancellationToken cancellationToken)
    {
        var validationErrors = await ValidateProviderRequestAsync(db, request.Name, request.PlaylistUrl, request.XmltvUrl, request.HeadersJson, request.TimeoutSeconds, request.AssociateToProfileIds, null, cancellationToken);
        if (validationErrors.Count > 0)
        {
            return TypedResults.ValidationProblem(validationErrors);
        }

        var now = DateTime.UtcNow;
        var provider = new Provider
        {
            ProviderId = Guid.NewGuid().ToString(),
            Name = request.Name.Trim(),
            Enabled = request.Enabled,
            PlaylistUrl = request.PlaylistUrl.Trim(),
            XmltvUrl = string.IsNullOrWhiteSpace(request.XmltvUrl) ? null : request.XmltvUrl.Trim(),
            HeadersJson = string.IsNullOrWhiteSpace(request.HeadersJson) ? null : request.HeadersJson,
            UserAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? null : request.UserAgent.Trim(),
            TimeoutSeconds = request.TimeoutSeconds,
            IncludeVod = request.IncludeVod,
            IncludeSeries = request.IncludeSeries,
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
            return TypedResults.Conflict("Provider could not be created due to a database conflict.");
        }

        var dto = (await BuildProviderDtosAsync(db, [provider], cancellationToken)).Single();

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Provider" });
        logger.LogInformation("Provider created: {ProviderId} '{Name}'.", provider.ProviderId, provider.Name);
        eventBus.Publish(AppEventKind.ProviderChanged);

        return TypedResults.Created($"/api/v1/providers/{provider.ProviderId}", dto);
    }

    private static async Task<Results<Ok<ProviderDto>, NotFound, ValidationProblem, Conflict<string>>> UpdateProviderAsync(
        string providerId,
        UpdateProviderRequest request,
        ApplicationDbContext db,
        AppEventBus eventBus,
        ILogger<ProviderApiLog> logger,
        CancellationToken cancellationToken)
    {
        var provider = await db.Providers.SingleOrDefaultAsync(x => x.ProviderId == providerId, cancellationToken);
        if (provider is null)
        {
            return TypedResults.NotFound();
        }

        var validationErrors = await ValidateProviderRequestAsync(db, request.Name, request.PlaylistUrl, request.XmltvUrl, request.HeadersJson, request.TimeoutSeconds, request.AssociateToProfileIds, providerId, cancellationToken);
        if (validationErrors.Count > 0)
        {
            return TypedResults.ValidationProblem(validationErrors);
        }

        provider.Name = request.Name.Trim();
        provider.PlaylistUrl = request.PlaylistUrl.Trim();
        provider.XmltvUrl = string.IsNullOrWhiteSpace(request.XmltvUrl) ? null : request.XmltvUrl.Trim();
        provider.HeadersJson = string.IsNullOrWhiteSpace(request.HeadersJson) ? null : request.HeadersJson;
        provider.UserAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? null : request.UserAgent.Trim();
        provider.Enabled = request.Enabled;
        provider.TimeoutSeconds = request.TimeoutSeconds;
        provider.IncludeVod = request.IncludeVod;
        provider.IncludeSeries = request.IncludeSeries;
        provider.UpdatedUtc = DateTime.UtcNow;

        var existingLinks = await db.ProfileProviders.Where(x => x.ProviderId == providerId).ToListAsync(cancellationToken);
        db.ProfileProviders.RemoveRange(existingLinks);
        ApplyProviderProfiles(db, providerId, request.AssociateToProfileIds);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return TypedResults.Conflict("Provider could not be updated due to a database conflict.");
        }

        var dto = (await BuildProviderDtosAsync(db, [provider], cancellationToken)).Single();

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Provider" });
        logger.LogInformation("Provider updated: {ProviderId} '{Name}'.", providerId, provider.Name);
        eventBus.Publish(AppEventKind.ProviderChanged);

        return TypedResults.Ok(dto);
    }

    private static async Task<Results<NoContent, NotFound, Conflict<string>>> DeleteProviderAsync(
        string providerId,
        ApplicationDbContext db,
        IRefreshTrigger refreshTrigger,
        AppEventBus eventBus,
        ILogger<ProviderApiLog> logger,
        CancellationToken cancellationToken)
    {
        if (refreshTrigger.IsRefreshing)
        {
            return TypedResults.Conflict("A snapshot refresh is currently in progress. Please wait for it to finish before deleting a provider.");
        }

        var provider = await db.Providers.SingleOrDefaultAsync(x => x.ProviderId == providerId, cancellationToken);
        if (provider is null)
        {
            return TypedResults.NotFound();
        }

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

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Provider" });
        logger.LogInformation("Provider deleted: {ProviderId} '{Name}'.", providerId, provider.Name);
        eventBus.Publish(AppEventKind.ProviderChanged);

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<ProviderDto>, NotFound>> GetProviderAsync(string providerId, ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var provider = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderId == providerId, cancellationToken);
        if (provider is null)
        {
            return TypedResults.NotFound();
        }

        var dto = (await BuildProviderDtosAsync(db, [provider], cancellationToken)).Single();
        return TypedResults.Ok(dto);
    }

    private static async Task<Results<Ok<ProviderEnabledResponse>, NotFound>> SetProviderEnabledAsync(
        string providerId,
        SetProviderEnabledRequest request,
        ApplicationDbContext db,
        AppEventBus eventBus,
        ILogger<ProviderApiLog> logger,
        CancellationToken cancellationToken)
    {
        var provider = await db.Providers.SingleOrDefaultAsync(x => x.ProviderId == providerId, cancellationToken);
        if (provider is null)
        {
            return TypedResults.NotFound();
        }

        provider.Enabled = request.Enabled;
        provider.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Provider" });
        logger.LogInformation("Provider {ProviderId} enabled={Enabled}.", providerId, provider.Enabled);
        eventBus.Publish(AppEventKind.ProviderChanged);

        return TypedResults.Ok(new ProviderEnabledResponse
        {
            ProviderId = provider.ProviderId,
            Enabled = provider.Enabled,
            UpdatedUtc = provider.UpdatedUtc,
        });
    }

    private static async Task<Results<Ok<ProviderActiveResponse>, NotFound, Conflict<string>>> SetProviderActiveAsync(
        string providerId,
        SetProviderActiveRequest request,
        ApplicationDbContext db,
        IRefreshTrigger refreshTrigger,
        AppEventBus eventBus,
        ILogger<ProviderApiLog> logger,
        CancellationToken cancellationToken)
    {
        if (refreshTrigger.IsRefreshing)
        {
            return TypedResults.Conflict("A snapshot refresh is currently in progress. Please wait for it to finish.");
        }

        var exists = await db.Providers.AnyAsync(x => x.ProviderId == providerId, cancellationToken);
        if (!exists)
        {
            return TypedResults.NotFound();
        }

        var now = DateTime.UtcNow;

        if (request.IsActive)
        {
            // Clear the current active provider first. SQLite evaluates the partial unique
            // index (is_active = 1) per-statement rather than at commit, so this must be a
            // separate SQL statement that completes before the activation below.
            await db.Providers
                .Where(x => x.IsActive && x.ProviderId != providerId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.IsActive, false)
                    .SetProperty(p => p.UpdatedUtc, now), cancellationToken);
        }

        // Activate (or deactivate) the target provider using a direct SQL UPDATE —
        // no SaveChangesAsync, no change-tracker ordering issues, no write-lock contention.
        await db.Providers
            .Where(x => x.ProviderId == providerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.IsActive, request.IsActive)
                .SetProperty(p => p.UpdatedUtc, now), cancellationToken);

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Provider" });
        logger.LogInformation("Provider {ProviderId} set active={IsActive}.", providerId, request.IsActive);
        eventBus.Publish(AppEventKind.ProviderChanged);

        if (request.IsActive)
        {
            eventBus.Publish(AppEventKind.ProviderActivated);
            refreshTrigger.TriggerRefresh();
        }

        return TypedResults.Ok(new ProviderActiveResponse
        {
            ProviderId = providerId,
            IsActive = request.IsActive,
            UpdatedUtc = now,
        });
    }

    private static async Task<Results<Ok<ProviderStatusDto>, NotFound>> GetProviderStatusAsync(
        string providerId,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var provider = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderId == providerId, cancellationToken);
        if (provider is null)
        {
            return TypedResults.NotFound();
        }

        var providerDto = (await BuildProviderDtosAsync(db, [provider], cancellationToken)).Single();

        return TypedResults.Ok(new ProviderStatusDto
        {
            ProviderId = providerId,
            LastRefresh = providerDto.LastRefresh,
            LatestSnapshots = providerDto.LatestSnapshots,
        });
    }

    // -------------------------------------------------------------------------
    // Config YAML Import
    // -------------------------------------------------------------------------

    private static async Task<Ok<List<ConfigYamlProviderDto>>> GetAvailableConfigProvidersAsync(
        ConfigYamlService configService,
        EnvironmentVariableService envVarService,
        CancellationToken cancellationToken)
    {
        var configProviders = await configService.LoadProvidersAsync();
        var dtos = configProviders.Select(p => new ConfigYamlProviderDto
        {
            Name = p.Name,
            PlaylistUrl = p.PlaylistUrl,
            XmltvUrl = p.XmltvUrl,
            RequiresEnvVars = envVarService.RequiresSubstitution(p.PlaylistUrl),
            MissingEnvVars = envVarService.ValidateVariables(p.PlaylistUrl).Missing.ToList(),
        }).ToList();

        return TypedResults.Ok(dtos);
    }

    private static async Task<Results<Ok<ProbeConfigProviderResultDto>, ValidationProblem>> ProbeConfigProviderAsync(
        ProbeConfigProviderRequest request,
        ConfigYamlService configService,
        EnvironmentVariableService envVarService,
        ProviderFetcher fetcher,
        CancellationToken cancellationToken)
    {
        var configProviders = await configService.LoadProvidersAsync();
        var configProvider = configProviders.FirstOrDefault(p => p.Name == request.Name);

        if (configProvider is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"Provider '{request.Name}' not found in config.yaml"]
            });
        }

        var (isValid, missing) = envVarService.ValidateVariables(configProvider.PlaylistUrl);
        if (!isValid)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["environment"] = [$"Missing environment variables: {string.Join(", ", missing)}"]
            });
        }

        var probe = new Provider
        {
            ProviderId = "(probe)",
            Name = configProvider.Name,
            PlaylistUrl = configProvider.PlaylistUrl,
            XmltvUrl = configProvider.XmltvUrl,
            HeadersJson = configProvider.Headers is not null
                ? JsonSerializer.Serialize(configProvider.Headers)
                : null,
            UserAgent = configProvider.UserAgent,
            TimeoutSeconds = configProvider.TimeoutSeconds,
            Enabled = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };

        try
        {
            var result = await fetcher.FetchPlaylistAsync(probe, cancellationToken);
            return TypedResults.Ok(new ProbeConfigProviderResultDto
            {
                Ok = true,
                ChannelCount = result.Channels.Count,
            });
        }
        catch (Exception ex) when (ex is ProviderFetchException or ProviderParseException)
        {
            return TypedResults.Ok(new ProbeConfigProviderResultDto
            {
                Ok = false,
                Error = ex.Message,
            });
        }
    }

    private static async Task<Results<Created<ProviderDto>, ValidationProblem, Conflict<string>>> ImportConfigProviderAsync(
        ImportConfigProviderRequest request,
        ConfigYamlService configService,
        EnvironmentVariableService envVarService,
        ApplicationDbContext db,
        AppEventBus eventBus,
        ILogger<ProviderApiLog> logger,
        CancellationToken cancellationToken)
    {
        var configProviders = await configService.LoadProvidersAsync();
        var configProvider = configProviders.FirstOrDefault(p => p.Name == request.Name);

        if (configProvider is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"Provider '{request.Name}' not found in config.yaml"]
            });
        }

        // Validate that all required env vars are defined
        var (isValid, missing) = envVarService.ValidateVariables(configProvider.PlaylistUrl);
        if (!isValid)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["environment"] = [$"Missing environment variables: {string.Join(", ", missing)}"]
            });
        }

        // Check if provider already exists
        var existing = await db.Providers.AsNoTracking().FirstOrDefaultAsync(p => p.Name == configProvider.Name, cancellationToken);
        if (existing is not null)
        {
            return TypedResults.Conflict($"Provider '{configProvider.Name}' already exists");
        }

        var now = DateTime.UtcNow;
        var provider = new Provider
        {
            ProviderId = Guid.NewGuid().ToString(),
            Name = configProvider.Name,
            PlaylistUrl = configProvider.PlaylistUrl,
            XmltvUrl = configProvider.XmltvUrl,
            HeadersJson = configProvider.Headers is not null
                ? JsonSerializer.Serialize(configProvider.Headers)
                : null,
            UserAgent = configProvider.UserAgent,
            TimeoutSeconds = configProvider.TimeoutSeconds,
            Enabled = configProvider.Enabled,
            IsActive = false,
            IncludeVod = request.IncludeVod,
            IncludeSeries = request.IncludeSeries,
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

        using var importScope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Provider" });
        logger.LogInformation("Provider imported from config: {ProviderId} '{Name}'.", provider.ProviderId, provider.Name);
        eventBus.Publish(AppEventKind.ProviderChanged);

        return TypedResults.Created($"/api/v1/providers/{provider.ProviderId}", dto);
    }

    // -------------------------------------------------------------------------
    // Provider Health Check
    // -------------------------------------------------------------------------

    private static async Task<Results<Ok<ProviderHealthDto>, NotFound>> GetProviderHealthAsync(
        string providerId,
        ApplicationDbContext db,
        EnvironmentVariableService envVarService,
        CancellationToken cancellationToken)
    {
        var provider = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderId == providerId, cancellationToken);
        if (provider is null)
        {
            return TypedResults.NotFound();
        }

        var (isValid, missing) = envVarService.ValidateVariables(provider.PlaylistUrl);
        var lastFetchRun = await db.FetchRuns
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId)
            .OrderByDescending(x => x.StartedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var health = new ProviderHealthDto
        {
            ProviderId = providerId,
            Name = provider.Name,
            CanFetch = isValid && provider.Enabled,
            Status = !provider.Enabled ? "disabled"
                : !isValid ? "missing-env-vars"
                : lastFetchRun?.Status == "ok" ? "healthy"
                : lastFetchRun is null ? "untested"
                : "unhealthy",
            MissingEnvVars = missing.ToList(),
            LastError = lastFetchRun?.ErrorSummary,
            LastSuccessFetch = lastFetchRun?.Status == "ok" ? lastFetchRun.FinishedUtc : null,
        };

        return TypedResults.Ok(health);
    }

    private static async Task<Results<Ok<ProviderPreviewDto>, NotFound, ProblemHttpResult, ValidationProblem>> GetPreviewAsync(
        string providerId,
        int? sampleSize,
        string? groupContains,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await db.Providers.AsNoTracking().AnyAsync(x => x.ProviderId == providerId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var sampleSizeValue = NormalizeSampleSize(sampleSize);
        if (sampleSizeValue is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["sampleSize"] = ["sampleSize must be between 1 and 50."]
            });
        }

        var latestOkFetchRun = await db.FetchRuns
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId && x.Status == "ok")
            .OrderByDescending(x => x.StartedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestOkFetchRun is null)
        {
            return TypedResults.Problem(
                title: "No preview data available",
                detail: "No successful provider refresh exists yet for this provider.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var preview = await BuildPreviewAsync(db, providerId, latestOkFetchRun.FetchRunId, latestOkFetchRun.StartedUtc, sampleSizeValue.Value, groupContains, cancellationToken);
        return TypedResults.Ok(preview);
    }

    private static async Task<Results<Ok<ProviderPreviewDto>, NotFound, ProblemHttpResult, ValidationProblem>> RefreshPreviewAsync(
        string providerId,
        RefreshPreviewRequest request,
        ApplicationDbContext db,
        ProviderFetcher fetcher,
        CancellationToken cancellationToken)
    {
        var provider = await db.Providers.SingleOrDefaultAsync(x => x.ProviderId == providerId, cancellationToken);
        if (provider is null)
        {
            return TypedResults.NotFound();
        }

        var sampleSizeValue = NormalizeSampleSize(request.SampleSize);
        if (sampleSizeValue is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["sampleSize"] = ["sampleSize must be between 1 and 50."]
            });
        }

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

            return TypedResults.Problem(
                title: "Provider fetch failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (ProviderParseException ex)
        {
            fetchRun.FinishedUtc = DateTime.UtcNow;
            fetchRun.ErrorSummary = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);

            return TypedResults.Problem(
                title: "Playlist parse failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        fetchRun.FinishedUtc = DateTime.UtcNow;
        fetchRun.Status = "ok";
        fetchRun.ErrorSummary = null;
        fetchRun.ChannelCountSeen = fetchResult.Channels.Count;
        fetchRun.PlaylistBytes = (int)Math.Min(fetchResult.Bytes, int.MaxValue);

        await db.SaveChangesAsync(CancellationToken.None);

        var preview = BuildPreviewFromParsed(providerId, fetchRun.StartedUtc, fetchRun.FetchRunId, fetchResult.Channels, sampleSizeValue.Value, request.GroupContains);
        return TypedResults.Ok(preview);
    }

    // -------------------------------------------------------------------------
    // Snapshots
    // -------------------------------------------------------------------------

    private static IResult TriggerRefreshAsync(IRefreshTrigger trigger, ILogger<ProviderApiLog> logger)
    {
        var triggered = trigger.TriggerRefresh();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Snapshot" });
        if (triggered)
        {
            logger.LogInformation("Snapshot refresh triggered manually.");
            return Results.Accepted();
        }
        logger.LogDebug("Snapshot refresh trigger ignored — refresh already in progress.");
        return Results.Conflict(new { message = "A refresh is already in progress." });
    }

    private static IResult TriggerBuildOnlyAsync(IRefreshTrigger trigger, ILogger<ProviderApiLog> logger)
    {
        var triggered = trigger.TriggerBuildOnly();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Snapshot" });
        if (triggered)
        {
            logger.LogInformation("Snapshot build-only triggered manually.");
            return Results.Accepted();
        }
        logger.LogDebug("Snapshot build-only trigger ignored — a refresh is already in progress.");
        return Results.Conflict(new { message = "A refresh is already in progress." });
    }

    // -------------------------------------------------------------------------
    // Shared upsert helpers (also used by SnapshotBuilder)
    // -------------------------------------------------------------------------

    internal static async Task UpsertProviderGroupsAsync(
        ApplicationDbContext db,
        string providerId,
        IReadOnlyList<ParsedProviderChannel> entries,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var groupNames = entries
            .Where(x => !string.IsNullOrWhiteSpace(x.GroupTitle))
            .Select(x => x.GroupTitle!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var existingGroups = await db.ProviderGroups
            .Where(x => x.ProviderId == providerId)
            .ToListAsync(cancellationToken);

        var byName = existingGroups.ToDictionary(x => x.RawName, StringComparer.Ordinal);

        foreach (var groupName in groupNames)
        {
            if (byName.TryGetValue(groupName, out var existing))
            {
                existing.LastSeenUtc = now;
                existing.Active = true;
                continue;
            }

            db.ProviderGroups.Add(new ProviderGroup
            {
                ProviderGroupId = Guid.NewGuid().ToString(),
                ProviderId = providerId,
                RawName = groupName,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                Active = true,
            });
        }

        foreach (var group in existingGroups)
        {
            if (!groupNames.Contains(group.RawName, StringComparer.Ordinal))
            {
                group.Active = false;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    internal static async Task UpsertProviderChannelsAsync(
        ApplicationDbContext db,
        string providerId,
        string fetchRunId,
        IReadOnlyList<ParsedProviderChannel> entries,
        DateTime now,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            await UpsertProviderChannelsCoreAsync(db, providerId, fetchRunId, entries, now, cancellationToken);
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = true;
        }
    }

    private static async Task UpsertProviderChannelsCoreAsync(
        ApplicationDbContext db,
        string providerId,
        string fetchRunId,
        IReadOnlyList<ParsedProviderChannel> entries,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var normalizedEntries = entries
            .Select(entry => new
            {
                Entry = entry,
                NormalizedKey = ProviderFetcher.NormalizeProviderChannelKey(entry.ProviderChannelKey),
            })
            .ToList();

        var groupLookup = await db.ProviderGroups
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId)
            .ToDictionaryAsync(x => x.RawName, x => x.ProviderGroupId, StringComparer.Ordinal, cancellationToken);

        var keys = normalizedEntries
            .Where(x => x.NormalizedKey is not null)
            .Select(x => x.NormalizedKey!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var existingByKey = keys.Count == 0
            ? new Dictionary<string, ProviderChannel>(StringComparer.Ordinal)
            : await db.ProviderChannels
                .Where(x => x.ProviderId == providerId && x.ProviderChannelKey != null && keys.Contains(x.ProviderChannelKey))
                .ToDictionaryAsync(x => x.ProviderChannelKey!, StringComparer.Ordinal, cancellationToken);

        var nullKeyChannels = await db.ProviderChannels
            .Where(x => x.ProviderId == providerId && x.ProviderChannelKey == null)
            .ToListAsync(cancellationToken);

        var existingByComposite = new Dictionary<string, ProviderChannel>(StringComparer.Ordinal);
        foreach (var channel in nullKeyChannels)
        {
            var composite = BuildNullKeyComposite(channel.DisplayName, channel.StreamUrl, channel.GroupTitle);
            if (!existingByComposite.ContainsKey(composite))
            {
                existingByComposite[composite] = channel;
            }
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in normalizedEntries)
        {
            var entry = item.Entry;
            var providerGroupId = entry.GroupTitle is not null && groupLookup.TryGetValue(entry.GroupTitle, out var foundGroupId)
                ? foundGroupId
                : null;

            ProviderChannel channel;
            if (item.NormalizedKey is not null)
            {
                if (!existingByKey.TryGetValue(item.NormalizedKey, out channel!))
                {
                    channel = new ProviderChannel
                    {
                        ProviderChannelId = Guid.NewGuid().ToString(),
                        ProviderId = providerId,
                        ProviderChannelKey = item.NormalizedKey,
                        FirstSeenUtc = now,
                    };

                    db.ProviderChannels.Add(channel);
                    existingByKey[item.NormalizedKey] = channel;
                }
            }
            else
            {
                var composite = BuildNullKeyComposite(entry.DisplayName, entry.StreamUrl, entry.GroupTitle);
                if (!existingByComposite.TryGetValue(composite, out channel!))
                {
                    channel = new ProviderChannel
                    {
                        ProviderChannelId = Guid.NewGuid().ToString(),
                        ProviderId = providerId,
                        ProviderChannelKey = null,
                        FirstSeenUtc = now,
                    };

                    db.ProviderChannels.Add(channel);
                    existingByComposite[composite] = channel;
                }
            }

            channel.ProviderChannelKey = item.NormalizedKey;
            channel.DisplayName = entry.DisplayName;
            channel.TvgId = entry.TvgId;
            channel.TvgName = entry.TvgName;
            channel.LogoUrl = entry.LogoUrl;
            channel.StreamUrl = entry.StreamUrl;
            channel.GroupTitle = entry.GroupTitle;
            channel.ProviderGroupId = providerGroupId;
            channel.IsEvent = false;
            channel.EventStartUtc = null;
            channel.EventEndUtc = null;
            channel.LastSeenUtc = now;
            channel.Active = true;
            channel.LastFetchRunId = fetchRunId;

            seenIds.Add(channel.ProviderChannelId);
        }

        var activeChannels = await db.ProviderChannels
            .Where(x => x.ProviderId == providerId && x.Active)
            .ToListAsync(cancellationToken);

        foreach (var channel in activeChannels)
        {
            if (!seenIds.Contains(channel.ProviderChannelId))
            {
                channel.Active = false;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static async Task<List<ProviderDto>> BuildProviderDtosAsync(ApplicationDbContext db, IReadOnlyCollection<Provider> providers, CancellationToken cancellationToken)
    {
        if (providers.Count == 0)
        {
            return [];
        }

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
                    IncludeVod = provider.IncludeVod,
                    IncludeSeries = provider.IncludeSeries,
                    AssociatedProfileIds = associatedProfileIds,
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

    private static async Task<Dictionary<string, string[]>> ValidateProviderRequestAsync(
        ApplicationDbContext db,
        string name,
        string playlistUrl,
        string? xmltvUrl,
        string? headersJson,
        int timeoutSeconds,
        List<string>? associateToProfileIds,
        string? providerId,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["name is required."];
        }

        if (string.IsNullOrWhiteSpace(playlistUrl) ||
            (!IsValidHttpUrl(playlistUrl) && !IsValidFileUri(playlistUrl)))
        {
            errors["playlistUrl"] = ["playlistUrl must be an http/https URL or a file:// URI."];
        }
        else if (IsValidFileUri(playlistUrl) && !File.Exists(new Uri(playlistUrl.Trim()).LocalPath))
        {
            errors["playlistUrl"] = ["Local file not found on the server."];
        }

        if (!string.IsNullOrWhiteSpace(xmltvUrl) &&
            !IsValidHttpUrl(xmltvUrl) && !IsValidFileUri(xmltvUrl))
        {
            errors["xmltvUrl"] = ["xmltvUrl must be an http/https URL or a file:// URI when provided."];
        }
        else if (!string.IsNullOrWhiteSpace(xmltvUrl) && IsValidFileUri(xmltvUrl) &&
                 !File.Exists(new Uri(xmltvUrl.Trim()).LocalPath))
        {
            errors["xmltvUrl"] = ["Local XMLTV file not found on the server."];
        }

        if (timeoutSeconds is < 1 or > 300)
        {
            errors["timeoutSeconds"] = ["timeoutSeconds must be between 1 and 300."];
        }

        if (!string.IsNullOrWhiteSpace(headersJson) && !TryValidateHeadersJson(headersJson, out var headersJsonError))
        {
            errors["headersJson"] = [headersJsonError!];
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var duplicateName = await db.Providers
                .AsNoTracking()
                .AnyAsync(x => x.Name == name.Trim() && x.ProviderId != providerId, cancellationToken);

            if (duplicateName)
            {
                errors["name"] = ["name must be unique."];
            }
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
            {
                errors["associateToProfileIds"] = ["One or more profile ids do not exist."];
            }
        }

        return errors;
    }

    private static async Task<string> GetUniqueProfileNameAsync(ApplicationDbContext db, string baseName, CancellationToken cancellationToken)
    {
        var name = baseName;
        var counter = 2;
        while (await db.Profiles.AsNoTracking().AnyAsync(p => p.Name == name, cancellationToken))
        {
            name = $"{baseName} {counter++}";
        }

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
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    private static bool IsValidFileUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeFile && !string.IsNullOrWhiteSpace(uri.LocalPath);
    }

    private static bool TryValidateHeadersJson(string value, out string? error)
    {
        error = null;

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                error = "headersJson must be a JSON object of string:string pairs.";
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != System.Text.Json.JsonValueKind.String)
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

    private static int? NormalizeSampleSize(int? value)
    {
        if (value is null)
        {
            return 10;
        }

        return value is < 1 or > 50 ? null : value;
    }

    private static string BuildNullKeyComposite(string displayName, string streamUrl, string? groupTitle)
        => $"{displayName}\u001f{streamUrl}\u001f{groupTitle}";

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

    internal static async Task<ProviderPreviewDto> BuildPreviewAsync(
        ApplicationDbContext db,
        string providerId,
        string fetchRunId,
        DateTime fetchStartedUtc,
        int sampleSize,
        string? groupContains,
        CancellationToken cancellationToken)
    {
        var channels = await db.ProviderChannels
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId && x.LastFetchRunId == fetchRunId)
            .Select(x => new PreviewChannelProjection
            {
                ProviderChannelId = x.ProviderChannelId,
                DisplayName = x.DisplayName,
                TvgId = x.TvgId,
                GroupName = (x.ProviderGroup != null ? x.ProviderGroup.RawName : x.GroupTitle) ?? string.Empty,
                StreamUrl = x.StreamUrl,
            })
            .ToListAsync(cancellationToken);

        var normalizedGroupFilter = string.IsNullOrWhiteSpace(groupContains) ? null : groupContains.Trim();

        var grouped = channels
            .Select(x => new PreviewChannelProjection
            {
                ProviderChannelId = x.ProviderChannelId,
                DisplayName = x.DisplayName,
                TvgId = x.TvgId,
                GroupName = string.IsNullOrWhiteSpace(x.GroupName) ? "(Ungrouped)" : x.GroupName!,
                StreamUrl = x.StreamUrl,
            })
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
                    .ThenBy(x => x.ProviderChannelId, StringComparer.Ordinal)
                    .Take(sampleSize)
                    .Select(x => new ProviderPreviewSampleChannelDto
                    {
                        ProviderChannelId = x.ProviderChannelId,
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

    internal static string? RedactStreamUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    private sealed class PreviewChannelProjection
    {
        public string ProviderChannelId { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? TvgId { get; init; }
        public string GroupName { get; init; } = string.Empty;
        public string? StreamUrl { get; init; }
    }
}

