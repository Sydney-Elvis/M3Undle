using System.Diagnostics;
using M3Undle.Web.Application;
using M3Undle.Web.Application.Epg;
using M3Undle.Web.Contracts.Epg;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Api;

public static class EpgApiEndpoints
{
    public static IEndpointRouteBuilder MapEpgApiEndpoints(this IEndpointRouteBuilder app)
    {
        var epg = app.MapGroup("/api/v1/epg");
        epg.RequireAuthorization(UiAccessPolicy.Name);
        epg.WithTags("EPG");

        // Sources
        epg.MapGet("/sources", ListSourcesAsync).WithSummary("List EPG sources");
        epg.MapPost("/sources", CreateSourceAsync).WithSummary("Create an EPG source");
        epg.MapPatch("/sources/{id}", UpdateSourceAsync).WithSummary("Update an EPG source");
        epg.MapDelete("/sources/{id}", DeleteSourceAsync).WithSummary("Delete an EPG source");
        epg.MapPost("/sources/{id}/test", TestSourceAsync).WithSummary("Test an EPG source");
        epg.MapPost("/sources/{id}/fetch", FetchSourceAsync).WithSummary("Fetch and persist an EPG source");
        epg.MapPost("/sources/{id}/auto-map", AutoMapSourceAsync).WithSummary("Auto-map EPG channels");
        epg.MapGet("/sources/{id}/channels", ListSourceChannelsAsync).WithSummary("List source channels");
        epg.MapGet("/sources/{id}/fetch-runs", ListFetchRunsAsync).WithSummary("List EPG fetch runs");

        // Mappings
        epg.MapGet("/mappings", ListMappingsAsync).WithSummary("List EPG mappings");
        epg.MapPut("/mappings", UpsertMappingAsync).WithSummary("Create or update an EPG mapping");
        epg.MapDelete("/mappings/{id}", DeleteMappingAsync).WithSummary("Delete an EPG mapping");

        return app;
    }

    // -------------------------------------------------------------------------
    // Sources
    // -------------------------------------------------------------------------

    private static async Task<Ok<List<EpgSourceDto>>> ListSourcesAsync(
        ApplicationDbContext db,
        string? providerId,
        CancellationToken cancellationToken)
    {
        var query = db.EpgSources.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(providerId))
            query = query.Where(x => x.ProviderId == providerId);

        var sources = await query
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Name)
            .Select(x => ToDto(x))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(sources);
    }

    private static async Task<Results<Created<EpgSourceDto>, ValidationProblem>> CreateSourceAsync(
        CreateEpgSourceRequest request,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = ["Name is required."]
            });

        if (request.Kind is not ("xmltv_url" or "xmltv_file" or "provider_xmltv"))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["kind"] = ["Kind must be xmltv_url, xmltv_file, or provider_xmltv."]
            });

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
            HeadersJson = request.HeadersJson,
            UserAgent = request.UserAgent?.Trim(),
            TimeoutSeconds = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : 30,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        db.EpgSources.Add(source);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/v1/epg/sources/{source.EpgSourceId}", ToDto(source));
    }

    private static async Task<Results<Ok<EpgSourceDto>, NotFound>> UpdateSourceAsync(
        string id,
        UpdateEpgSourceRequest request,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var source = await db.EpgSources.FirstOrDefaultAsync(x => x.EpgSourceId == id, cancellationToken);
        if (source is null)
            return TypedResults.NotFound();

        if (request.Name is not null)
            source.Name = request.Name.Trim();
        if (request.UrlOrPath is not null)
            source.UrlOrPath = request.UrlOrPath.Trim();
        if (request.Priority.HasValue)
            source.Priority = request.Priority.Value;
        if (request.Enabled.HasValue)
            source.Enabled = request.Enabled.Value;
        if (request.HeadersJson is not null)
            source.HeadersJson = request.HeadersJson;
        if (request.UserAgent is not null)
            source.UserAgent = request.UserAgent.Trim();
        if (request.TimeoutSeconds.HasValue)
            source.TimeoutSeconds = request.TimeoutSeconds.Value > 0 ? request.TimeoutSeconds.Value : 30;

        source.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(ToDto(source));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteSourceAsync(
        string id,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var source = await db.EpgSources.FirstOrDefaultAsync(x => x.EpgSourceId == id, cancellationToken);
        if (source is null)
            return TypedResults.NotFound();

        db.EpgSources.Remove(source);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<EpgSourceTestResult>, NotFound>> TestSourceAsync(
        string id,
        ApplicationDbContext db,
        EpgSourceFetcher epgSourceFetcher,
        XmltvParser xmltvParser,
        RuntimePaths runtimePaths,
        CancellationToken cancellationToken)
    {
        var source = await db.EpgSources
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.EpgSourceId == id, cancellationToken);

        if (source is null)
            return TypedResults.NotFound();

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
            return TypedResults.Ok(new EpgSourceTestResult
            {
                Success = false,
                Error = result.ErrorSummary ?? "Fetch failed.",
                ProviderId = source.ProviderId,
                CachedXmlAvailable = File.Exists(cacheFile),
                ElapsedMs = (int)sw.ElapsedMilliseconds,
            });
        }

        var catalogue = string.IsNullOrWhiteSpace(xml)
            ? EpgCatalogue.Empty(source.EpgSourceId)
            : xmltvParser.Parse(source.EpgSourceId, xml);

        var allProgrammes = catalogue.ProgrammesByChannel.Values.SelectMany(p => p).ToList();
        return TypedResults.Ok(new EpgSourceTestResult
        {
            Success = true,
            ProviderId = source.ProviderId,
            ChannelCount = catalogue.Channels.Count,
            ProgrammeCount = allProgrammes.Count,
            EarliestProgramme = allProgrammes.Count > 0 ? allProgrammes.Min(p => p.StartUtc) : null,
            LatestProgramme = allProgrammes.Count > 0 ? allProgrammes.Max(p => p.StopUtc) : null,
            Bytes = result.Bytes,
            ElapsedMs = (int)sw.ElapsedMilliseconds,
            CachedXmlAvailable = File.Exists(cacheFile),
        });
    }

    private static async Task<Results<Ok<EpgFetchRunDto>, NotFound>> FetchSourceAsync(
        string id,
        ApplicationDbContext db,
        EpgSourceFetcher epgSourceFetcher,
        XmltvParser xmltvParser,
        RuntimePaths runtimePaths,
        CancellationToken cancellationToken)
    {
        var source = await db.EpgSources
            .FirstOrDefaultAsync(x => x.EpgSourceId == id, cancellationToken);

        if (source is null)
            return TypedResults.NotFound();

        Provider? provider = null;
        if (source.ProviderId is not null)
            provider = await db.Providers.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProviderId == source.ProviderId, cancellationToken);

        var startedUtc = DateTime.UtcNow;
        var cacheFile = Path.Combine(runtimePaths.DataDirectory, "epg-cache", $"{source.EpgSourceId}.xml");

        var (result, xml) = await epgSourceFetcher.FetchAsync(source, provider, cacheFile, cancellationToken);

        var finishedUtc = DateTime.UtcNow;

        // Update source metadata columns
        if (result.Status is "ok" or "not_modified")
        {
            source.LastSuccessUtc = finishedUtc;
            source.ETag = result.ETag ?? source.ETag;
            source.LastModifiedUtc = result.LastModifiedUtc ?? source.LastModifiedUtc;
        }
        else
        {
            source.LastFailureUtc = finishedUtc;
        }
        source.UpdatedUtc = finishedUtc;

        // Upsert source channels from parsed catalogue
        var channelCount = 0;
        var programmeCount = 0;
        if (!string.IsNullOrWhiteSpace(xml))
        {
            var catalogue = xmltvParser.Parse(source.EpgSourceId, xml);
            channelCount = catalogue.Channels.Count;
            programmeCount = catalogue.ProgrammesByChannel.Values.Sum(p => p.Count);

            var now = DateTime.UtcNow;
            var existing = await db.EpgSourceChannels
                .Where(x => x.EpgSourceId == id)
                .ToListAsync(cancellationToken);
            var byId = existing.ToDictionary(x => x.XmltvChannelId, StringComparer.Ordinal);
            var addedThisRun = new HashSet<string>(StringComparer.Ordinal);

            foreach (var ch in catalogue.Channels)
            {
                if (byId.TryGetValue(ch.XmltvChannelId, out var row))
                {
                    row.DisplayName = ch.DisplayName;
                    row.IconUrl = ch.IconUrl;
                    row.LastSeenUtc = now;
                }
                else if (addedThisRun.Add(ch.XmltvChannelId))
                {
                    db.EpgSourceChannels.Add(new EpgSourceChannel
                    {
                        EpgSourceChannelId = Guid.NewGuid().ToString(),
                        EpgSourceId = id,
                        XmltvChannelId = ch.XmltvChannelId,
                        DisplayName = ch.DisplayName,
                        IconUrl = ch.IconUrl,
                        LastSeenUtc = now,
                    });
                }
            }
        }

        var fetchRun = new EpgFetchRun
        {
            EpgFetchRunId = Guid.NewGuid().ToString(),
            EpgSourceId = id,
            StartedUtc = startedUtc,
            FinishedUtc = finishedUtc,
            Status = result.Status,
            Bytes = result.Bytes > 0 ? (int)Math.Min(result.Bytes, int.MaxValue) : null,
            ChannelCount = channelCount > 0 ? channelCount : null,
            ProgrammeCount = programmeCount > 0 ? programmeCount : null,
            ErrorSummary = result.ErrorSummary,
        };
        db.EpgFetchRuns.Add(fetchRun);

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new EpgFetchRunDto
        {
            EpgFetchRunId = fetchRun.EpgFetchRunId,
            EpgSourceId = fetchRun.EpgSourceId,
            StartedUtc = fetchRun.StartedUtc,
            FinishedUtc = fetchRun.FinishedUtc,
            Status = fetchRun.Status,
            Bytes = fetchRun.Bytes,
            ChannelCount = fetchRun.ChannelCount,
            ProgrammeCount = fetchRun.ProgrammeCount,
            ErrorSummary = fetchRun.ErrorSummary,
        });
    }

    private static async Task<Results<Ok, NotFound>> AutoMapSourceAsync(
        string id,
        string? profileId,
        ApplicationDbContext db,
        EpgChannelMapper epgChannelMapper,
        XmltvParser xmltvParser,
        RuntimePaths runtimePaths,
        CancellationToken cancellationToken)
    {
        var source = await db.EpgSources
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.EpgSourceId == id, cancellationToken);

        if (source is null || source.ProviderId is null)
            return TypedResults.NotFound();

        // If no profileId given, use the first profile linked to this provider
        if (string.IsNullOrWhiteSpace(profileId))
        {
            profileId = await db.ProfileProviders
                .AsNoTracking()
                .Where(x => x.ProviderId == source.ProviderId && x.Enabled)
                .OrderBy(x => x.Priority)
                .Select(x => x.ProfileId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(profileId))
            return TypedResults.NotFound();

        // Load cached XML
        var cacheFile = Path.Combine(runtimePaths.DataDirectory, "epg-cache", $"{source.EpgSourceId}.xml");
        if (!File.Exists(cacheFile))
            return TypedResults.Ok();

        var xml = await File.ReadAllTextAsync(cacheFile, cancellationToken);
        var catalogue = xmltvParser.Parse(source.EpgSourceId, xml);

        await epgChannelMapper.AutoMapAsync(profileId, source.ProviderId, [catalogue], cancellationToken);

        return TypedResults.Ok();
    }

    private static async Task<Ok<List<EpgSourceChannelDto>>> ListSourceChannelsAsync(
        string id,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var channels = await db.EpgSourceChannels
            .AsNoTracking()
            .Where(x => x.EpgSourceId == id)
            .OrderBy(x => x.DisplayName)
            .Select(x => new EpgSourceChannelDto
            {
                EpgSourceChannelId = x.EpgSourceChannelId,
                XmltvChannelId = x.XmltvChannelId,
                DisplayName = x.DisplayName,
                IconUrl = x.IconUrl,
            })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(channels);
    }

    private static async Task<Ok<List<EpgFetchRunDto>>> ListFetchRunsAsync(
        string id,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var runs = await db.EpgFetchRuns
            .AsNoTracking()
            .Where(x => x.EpgSourceId == id)
            .OrderByDescending(x => x.StartedUtc)
            .Take(20)
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

        return TypedResults.Ok(runs);
    }

    // -------------------------------------------------------------------------
    // Mappings
    // -------------------------------------------------------------------------

    private static async Task<Ok<List<EpgChannelMappingDto>>> ListMappingsAsync(
        ApplicationDbContext db,
        string? profileId,
        string? providerChannelId,
        CancellationToken cancellationToken)
    {
        var query = db.EpgChannelMappings
            .AsNoTracking()
            .Include(x => x.ProviderChannel)
            .Include(x => x.EpgSource)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(profileId))
            query = query.Where(x => x.ProfileId == profileId);

        if (!string.IsNullOrWhiteSpace(providerChannelId))
            query = query.Where(x => x.ProviderChannelId == providerChannelId);

        var mappings = await query
            .OrderBy(x => x.ProviderChannel.DisplayName)
            .ThenBy(x => x.EpgSource.Priority)
            .Select(x => new EpgChannelMappingDto
            {
                EpgChannelMappingId = x.EpgChannelMappingId,
                ProfileId = x.ProfileId,
                ProviderChannelId = x.ProviderChannelId,
                ProviderChannelDisplayName = x.ProviderChannel.DisplayName,
                ProviderChannelTvgId = x.ProviderChannel.TvgId,
                EpgSourceId = x.EpgSourceId,
                EpgSourceName = x.EpgSource.Name,
                XmltvChannelId = x.XmltvChannelId,
                MappingMode = x.MappingMode,
                Confidence = x.Confidence,
            })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(mappings);
    }

    private static async Task<Results<Ok<EpgChannelMappingDto>, ValidationProblem>> UpsertMappingAsync(
        UpsertEpgMappingRequest request,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileId) ||
            string.IsNullOrWhiteSpace(request.ProviderChannelId) ||
            string.IsNullOrWhiteSpace(request.EpgSourceId) ||
            string.IsNullOrWhiteSpace(request.XmltvChannelId))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = ["profileId, providerChannelId, epgSourceId, and xmltvChannelId are required."]
            });
        }

        var now = DateTime.UtcNow;
        var existing = await db.EpgChannelMappings
            .FirstOrDefaultAsync(x =>
                x.ProfileId == request.ProfileId &&
                x.ProviderChannelId == request.ProviderChannelId &&
                x.EpgSourceId == request.EpgSourceId,
                cancellationToken);

        if (existing is not null)
        {
            existing.XmltvChannelId = request.XmltvChannelId;
            existing.MappingMode = "manual";
            existing.Confidence = 1.0f;
            existing.UpdatedUtc = now;
        }
        else
        {
            existing = new EpgChannelMapping
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
            };
            db.EpgChannelMappings.Add(existing);
        }

        await db.SaveChangesAsync(cancellationToken);

        var dto = await db.EpgChannelMappings
            .AsNoTracking()
            .Include(x => x.ProviderChannel)
            .Include(x => x.EpgSource)
            .Where(x => x.EpgChannelMappingId == existing.EpgChannelMappingId)
            .Select(x => new EpgChannelMappingDto
            {
                EpgChannelMappingId = x.EpgChannelMappingId,
                ProfileId = x.ProfileId,
                ProviderChannelId = x.ProviderChannelId,
                ProviderChannelDisplayName = x.ProviderChannel.DisplayName,
                ProviderChannelTvgId = x.ProviderChannel.TvgId,
                EpgSourceId = x.EpgSourceId,
                EpgSourceName = x.EpgSource.Name,
                XmltvChannelId = x.XmltvChannelId,
                MappingMode = x.MappingMode,
                Confidence = x.Confidence,
            })
            .FirstAsync(cancellationToken);

        return TypedResults.Ok(dto);
    }

    private static async Task<Results<NoContent, NotFound>> DeleteMappingAsync(
        string id,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var mapping = await db.EpgChannelMappings
            .FirstOrDefaultAsync(x => x.EpgChannelMappingId == id, cancellationToken);

        if (mapping is null)
            return TypedResults.NotFound();

        db.EpgChannelMappings.Remove(mapping);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
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
