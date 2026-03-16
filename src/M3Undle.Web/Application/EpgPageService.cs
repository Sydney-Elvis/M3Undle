using M3Undle.Web.Application.Epg;
using M3Undle.Web.Contracts.Epg;
using M3Undle.Web.Contracts.Providers;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace M3Undle.Web.Application;

public sealed class EpgPageService(
    IServiceScopeFactory scopeFactory,
    EpgSourceFetcher epgSourceFetcher,
    XmltvParser xmltvParser,
    RuntimePaths runtimePaths,
    ILogger<EpgPageService> logger)
{
    private readonly ILogger<EpgPageService> _logger = logger;

    // -------------------------------------------------------------------------
    // Page data
    // -------------------------------------------------------------------------

    public async Task<List<ProviderListItemDto>> GetProvidersAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Providers
            .AsNoTracking()
            .Where(x => x.Enabled)
            .OrderByDescending(x => x.IsActive)  // active provider first
            .ThenBy(x => x.Name)
            .Select(x => new ProviderListItemDto
            {
                ProviderId = x.ProviderId,
                Name = x.Name,
                IsActive = x.IsActive,
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<List<EpgSourceDto>> GetSourcesAsync(string? providerId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var query = db.EpgSources.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(providerId))
            query = query.Where(x => x.ProviderId == providerId);
        else
            query = query.Where(x => x.ProviderId == null);

        var sources = await query
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Name)
            .Select(x => ToDto(x))
            .ToListAsync(cancellationToken);

        // Fetch the most recent error summary for any source in a failed state
        var failedIds = sources
            .Where(s => s.LastFailureUtc.HasValue &&
                        (s.LastSuccessUtc is null || s.LastFailureUtc > s.LastSuccessUtc))
            .Select(s => s.EpgSourceId)
            .ToList();

        if (failedIds.Count > 0)
        {
            var latestErrors = await db.EpgFetchRuns
                .AsNoTracking()
                .Where(r => failedIds.Contains(r.EpgSourceId) &&
                            r.Status == "fail" &&
                            r.ErrorSummary != null)
                .GroupBy(r => r.EpgSourceId)
                .Select(g => new
                {
                    SourceId = g.Key,
                    Error = g.OrderByDescending(r => r.StartedUtc).First().ErrorSummary,
                })
                .ToDictionaryAsync(x => x.SourceId, x => x.Error, cancellationToken);

            foreach (var dto in sources)
            {
                if (latestErrors.TryGetValue(dto.EpgSourceId, out var err))
                    dto.LastErrorSummary = err;
            }
        }

        return sources;
    }

    // -------------------------------------------------------------------------
    // Source CRUD
    // -------------------------------------------------------------------------

    public async Task<(EpgSourceDto? Dto, string? Error)> CreateSourceAsync(
        CreateEpgSourceRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return (null, "Name is required.");

        if (request.Kind is not ("xmltv_url" or "xmltv_file" or "provider_xmltv"))
            return (null, "Kind must be xmltv_url, xmltv_file, or provider_xmltv.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var now = DateTime.UtcNow;
        var source = new EpgSource
        {
            EpgSourceId = Guid.NewGuid().ToString(),
            ProviderId = request.ProviderId,
            Name = request.Name.Trim(),
            Kind = request.Kind,
            UrlOrPath = request.UrlOrPath?.Trim(),
            Priority = request.Priority,
            Enabled = request.Enabled,
            HeadersJson = request.HeadersJson?.Trim(),
            UserAgent = request.UserAgent?.Trim(),
            TimeoutSeconds = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : 30,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        db.EpgSources.Add(source);
        await db.SaveChangesAsync(cancellationToken);
        return (ToDto(source), null);
    }

    public async Task<(EpgSourceDto? Dto, string? Error)> UpdateSourceAsync(
        string id, UpdateEpgSourceRequest request, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var source = await db.EpgSources.FirstOrDefaultAsync(x => x.EpgSourceId == id, cancellationToken);
        if (source is null)
            return (null, "Source not found.");

        if (request.Name is not null) source.Name = request.Name.Trim();
        if (request.UrlOrPath is not null) source.UrlOrPath = request.UrlOrPath.Trim();
        if (request.Priority.HasValue) source.Priority = request.Priority.Value;
        if (request.Enabled.HasValue) source.Enabled = request.Enabled.Value;
        if (request.HeadersJson is not null) source.HeadersJson = request.HeadersJson.Trim();
        if (request.UserAgent is not null) source.UserAgent = request.UserAgent.Trim();
        if (request.TimeoutSeconds.HasValue)
            source.TimeoutSeconds = request.TimeoutSeconds.Value > 0 ? request.TimeoutSeconds.Value : 30;

        source.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return (ToDto(source), null);
    }

    public async Task<string?> DeleteSourceAsync(string id, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var source = await db.EpgSources.FirstOrDefaultAsync(x => x.EpgSourceId == id, cancellationToken);
        if (source is null)
            return "Source not found.";

        db.EpgSources.Remove(source);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // -------------------------------------------------------------------------
    // Test + Auto-map
    // -------------------------------------------------------------------------

    public async Task<EpgSourceTestResult> TestSourceAsync(string id, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var source = await db.EpgSources.AsNoTracking()
            .FirstOrDefaultAsync(x => x.EpgSourceId == id, cancellationToken);

        if (source is null)
            return new EpgSourceTestResult { Success = false, Error = "Source not found." };

        Provider? provider = null;
        if (source.ProviderId is not null)
            provider = await db.Providers.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProviderId == source.ProviderId, cancellationToken);

        var sw = Stopwatch.StartNew();
        var cacheFile = Path.Combine(runtimePaths.DataDirectory, "epg-cache", $"{source.EpgSourceId}.xml");

        var (result, xml) = await epgSourceFetcher.FetchAsync(source, provider, cacheFile, cancellationToken);
        sw.Stop();

        if (!result.Status.Equals("ok", StringComparison.Ordinal) &&
            !result.Status.Equals("not_modified", StringComparison.Ordinal))
        {
            return new EpgSourceTestResult
            {
                Success = false,
                Error = result.ErrorSummary ?? "Fetch failed.",
                ElapsedMs = (int)sw.ElapsedMilliseconds,
            };
        }

        var catalogue = string.IsNullOrWhiteSpace(xml)
            ? EpgCatalogue.Empty(source.EpgSourceId)
            : xmltvParser.Parse(source.EpgSourceId, xml);

        var allProgrammes = catalogue.ProgrammesByChannel.Values.SelectMany(p => p).ToList();
        return new EpgSourceTestResult
        {
            Success = true,
            ChannelCount = catalogue.Channels.Count,
            ProgrammeCount = allProgrammes.Count,
            EarliestProgramme = allProgrammes.Count > 0 ? allProgrammes.Min(p => p.StartUtc) : null,
            LatestProgramme = allProgrammes.Count > 0 ? allProgrammes.Max(p => p.StopUtc) : null,
            Bytes = result.Bytes,
            ElapsedMs = (int)sw.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// Runs EPG auto-mapping for all cached sources linked to <paramref name="providerId"/>
    /// within <paramref name="profileId"/>. Used after provider channels are mapped to output.
    /// </summary>
    public async Task AutoMapForProviderAsync(string profileId, string providerId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mapper = scope.ServiceProvider.GetRequiredService<EpgChannelMapper>();

        var sources = await db.EpgSources
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId && x.Enabled)
            .OrderBy(x => x.Priority)
            .ToListAsync(cancellationToken);

        if (sources.Count == 0)
            return;

        var catalogues = new List<EpgCatalogue>();
        foreach (var source in sources)
        {
            var cacheFile = Path.Combine(runtimePaths.DataDirectory, "epg-cache", $"{source.EpgSourceId}.xml");
            if (!File.Exists(cacheFile))
                continue;

            try
            {
                var xml = await File.ReadAllTextAsync(cacheFile, cancellationToken);
                catalogues.Add(xmltvParser.Parse(source.EpgSourceId, xml));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EPG auto-map: failed to parse cache for source {EpgSourceId}.", source.EpgSourceId);
            }
        }

        if (catalogues.Count == 0)
        {
            _logger.LogDebug("EPG auto-map skipped for provider {ProviderId}: no cached EPG data.", providerId);
            return;
        }

        await mapper.AutoMapAsync(profileId, providerId, catalogues, cancellationToken);
    }

    public async Task<string?> AutoMapAsync(string sourceId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mapper = scope.ServiceProvider.GetRequiredService<EpgChannelMapper>();

        var source = await db.EpgSources.AsNoTracking()
            .FirstOrDefaultAsync(x => x.EpgSourceId == sourceId, cancellationToken);

        if (source is null || source.ProviderId is null)
            return "Source not found or not linked to a provider.";

        var profileId = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.ProviderId == source.ProviderId && x.Enabled)
            .OrderBy(x => x.Priority)
            .Select(x => x.ProfileId)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(profileId))
            return "No active profile found for this provider.";

        var cacheFile = Path.Combine(runtimePaths.DataDirectory, "epg-cache", $"{source.EpgSourceId}.xml");
        if (!File.Exists(cacheFile))
            return "No cached data yet — run a test first to fetch the source.";

        var xml = await File.ReadAllTextAsync(cacheFile, cancellationToken);
        var catalogue = xmltvParser.Parse(sourceId, xml);
        await mapper.AutoMapAsync(profileId, source.ProviderId, [catalogue], cancellationToken);
        return null;
    }

    // -------------------------------------------------------------------------
    // Fetch runs + source channels
    // -------------------------------------------------------------------------

    public async Task<List<EpgFetchRunDto>> GetFetchRunsAsync(string sourceId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.EpgFetchRuns
            .AsNoTracking()
            .Where(x => x.EpgSourceId == sourceId)
            .OrderByDescending(x => x.StartedUtc)
            .Take(10)
            .Select(x => new EpgFetchRunDto
            {
                EpgFetchRunId = x.EpgFetchRunId,
                EpgSourceId = x.EpgSourceId,
                StartedUtc = x.StartedUtc,
                FinishedUtc = x.FinishedUtc,
                Status = x.Status,
                Bytes = x.Bytes,
                ChannelCount = x.ChannelCount,
                ProgrammeCount = x.ProgrammeCount,
                ErrorSummary = x.ErrorSummary,
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<List<EpgSourceChannelDto>> GetSourceChannelsAsync(string sourceId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.EpgSourceChannels
            .AsNoTracking()
            .Where(x => x.EpgSourceId == sourceId)
            .OrderBy(x => x.DisplayName)
            .Select(x => new EpgSourceChannelDto
            {
                EpgSourceChannelId = x.EpgSourceChannelId,
                XmltvChannelId = x.XmltvChannelId,
                DisplayName = x.DisplayName,
                IconUrl = x.IconUrl,
            })
            .ToListAsync(cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Mappings
    // -------------------------------------------------------------------------

    public async Task<EpgMappingsPageData> GetMappingsPageDataAsync(
        string providerId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Profiles linked to this provider
        var profiles = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId && x.Enabled)
            .Join(db.Profiles, pp => pp.ProfileId, p => p.ProfileId, (pp, p) => new ProfileListItemDto
            {
                ProfileId = p.ProfileId,
                Name = p.Name,
            })
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        // Sources for this provider
        var sources = await db.EpgSources
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId)
            .OrderBy(x => x.Priority)
            .Select(x => ToDto(x))
            .ToListAsync(cancellationToken);

        return new EpgMappingsPageData(profiles, sources);
    }

    public async Task<List<EpgChannelMappingDto>> GetMappingsAsync(
        string profileId, string providerId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Only show channels that are selected for output in this profile:
        // ProfileGroupChannelFilter → ProfileGroupFilter where Decision == "hold" and ProfileId == profileId
        var configuredChannelIds = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Where(f => f.ProfileGroupFilter.ProfileId == profileId &&
                        f.ProfileGroupFilter.Decision == "hold")
            .Select(f => f.ProviderChannelId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Live channels for this provider that are in the configured output set
        var channels = await db.ProviderChannels
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId && x.Active && x.ContentType == "live"
                        && configuredChannelIds.Contains(x.ProviderChannelId))
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        // Get all mappings for this profile's channels
        var channelIds = channels.Select(c => c.ProviderChannelId).ToHashSet();
        var mappings = await db.EpgChannelMappings
            .AsNoTracking()
            .Include(x => x.EpgSource)
            .Where(x => x.ProfileId == profileId && channelIds.Contains(x.ProviderChannelId))
            .ToListAsync(cancellationToken);

        var mappingLookup = mappings
            .GroupBy(m => m.ProviderChannelId)
            .ToDictionary(g => g.Key, g => g.First());

        // Build a row per channel, whether mapped or not
        var result = new List<EpgChannelMappingDto>(channels.Count);
        foreach (var ch in channels)
        {
            if (mappingLookup.TryGetValue(ch.ProviderChannelId, out var m))
            {
                result.Add(new EpgChannelMappingDto
                {
                    EpgChannelMappingId = m.EpgChannelMappingId,
                    ProfileId = m.ProfileId,
                    ProviderChannelId = m.ProviderChannelId,
                    ProviderChannelDisplayName = ch.DisplayName,
                    ProviderChannelTvgId = ch.TvgId,
                    EpgSourceId = m.EpgSourceId,
                    EpgSourceName = m.EpgSource?.Name ?? string.Empty,
                    XmltvChannelId = m.XmltvChannelId,
                    MappingMode = m.MappingMode,
                    Confidence = m.Confidence,
                });
            }
            else
            {
                result.Add(new EpgChannelMappingDto
                {
                    EpgChannelMappingId = string.Empty,
                    ProfileId = profileId,
                    ProviderChannelId = ch.ProviderChannelId,
                    ProviderChannelDisplayName = ch.DisplayName,
                    ProviderChannelTvgId = ch.TvgId,
                    EpgSourceId = string.Empty,
                    EpgSourceName = string.Empty,
                    XmltvChannelId = string.Empty,
                    MappingMode = "none",
                    Confidence = 0f,
                });
            }
        }

        return result;
    }

    public async Task<string?> UpsertMappingAsync(UpsertEpgMappingRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileId) ||
            string.IsNullOrWhiteSpace(request.ProviderChannelId) ||
            string.IsNullOrWhiteSpace(request.EpgSourceId) ||
            string.IsNullOrWhiteSpace(request.XmltvChannelId))
            return "All fields are required.";

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var now = DateTime.UtcNow;
        var existing = await db.EpgChannelMappings.FirstOrDefaultAsync(x =>
            x.ProfileId == request.ProfileId &&
            x.ProviderChannelId == request.ProviderChannelId &&
            x.EpgSourceId == request.EpgSourceId, cancellationToken);

        if (existing is not null)
        {
            existing.XmltvChannelId = request.XmltvChannelId;
            existing.MappingMode = "manual";
            existing.Confidence = 1.0f;
            existing.UpdatedUtc = now;
        }
        else
        {
            db.EpgChannelMappings.Add(new EpgChannelMapping
            {
                EpgChannelMappingId = Guid.NewGuid().ToString(),
                ProfileId = request.ProfileId,
                ProviderChannelId = request.ProviderChannelId,
                EpgSourceId = request.EpgSourceId,
                XmltvChannelId = request.XmltvChannelId,
                MappingMode = "manual",
                Confidence = 1.0f,
                CreatedUtc = now,
                UpdatedUtc = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public async Task<string?> DeleteMappingAsync(string id, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var mapping = await db.EpgChannelMappings
            .FirstOrDefaultAsync(x => x.EpgChannelMappingId == id, cancellationToken);
        if (mapping is null)
            return "Mapping not found.";

        db.EpgChannelMappings.Remove(mapping);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static EpgSourceDto ToDto(EpgSource s) => new()
    {
        EpgSourceId = s.EpgSourceId,
        ProviderId = s.ProviderId,
        Name = s.Name,
        Kind = s.Kind,
        UrlOrPath = s.UrlOrPath,
        Priority = s.Priority,
        Enabled = s.Enabled,
        HeadersJson = s.HeadersJson,
        UserAgent = s.UserAgent,
        TimeoutSeconds = s.TimeoutSeconds,
        LastSuccessUtc = s.LastSuccessUtc,
        LastFailureUtc = s.LastFailureUtc,
        CreatedUtc = s.CreatedUtc,
        UpdatedUtc = s.UpdatedUtc,
    };
}

public sealed record EpgMappingsPageData(
    List<ProfileListItemDto> Profiles,
    List<EpgSourceDto> Sources);
