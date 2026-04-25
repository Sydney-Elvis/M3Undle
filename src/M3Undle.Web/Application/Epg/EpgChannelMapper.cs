using M3Undle.Core.Epg;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Epg;

/// <summary>
/// Maps provider channels to EPG source channels using a priority-ordered
/// matching strategy: exact tvg-id → display name → fuzzy token overlap.
/// Stores auto-mapping results in the DB; manual overrides are never replaced.
/// </summary>
public sealed class EpgChannelMapper(
    ApplicationDbContext db,
    ILogger<EpgChannelMapper> logger)
{
    /// <summary>
    /// Runs auto-mapping for all channels of <paramref name="providerId"/> against
    /// all enabled EPG sources for that provider within <paramref name="profileId"/>.
    /// Existing manual mappings are preserved.
    /// </summary>
    public async Task AutoMapAsync(
        string profileId,
        string providerId,
        IReadOnlyList<EpgCatalogue> catalogues,
        CancellationToken cancellationToken)
    {
        if (catalogues.Count == 0)
            return;

        // Load live provider channels for this provider
        var providerChannels = await db.ProviderChannels
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId && x.Active && x.ContentType == "live")
            .ToListAsync(cancellationToken);

        if (providerChannels.Count == 0)
            return;

        // Load existing mappings to avoid overwriting manual ones
        var existingMappings = await db.EpgChannelMappings
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId)
            .ToListAsync(cancellationToken);

        var manualKeys = existingMappings
            .Where(m => m.MappingMode == "manual")
            .Select(m => (m.ProviderChannelId, m.EpgSourceId))
            .ToHashSet();

        var autoKeys = existingMappings
            .Where(m => m.MappingMode != "manual")
            .ToDictionary(m => (m.ProviderChannelId, m.EpgSourceId));

        var now = DateTime.UtcNow;
        int created = 0, updated = 0;

        foreach (var catalogue in catalogues)
        {
            var sourceChannels = catalogue.Channels;
            if (sourceChannels.Count == 0)
                continue;

            foreach (var channel in providerChannels)
            {
                var key = (channel.ProviderChannelId, catalogue.SourceId);

                // Never overwrite manual mappings
                if (manualKeys.Contains(key))
                    continue;

                var match = EpgChannelMatcher.FindBestMatch(
                    new EpgChannelMatchCandidate(channel.DisplayName, channel.TvgId, channel.TvgName),
                    sourceChannels);
                if (match is null)
                    continue;

                if (autoKeys.TryGetValue(key, out var existing))
                {
                    // Update only if we have a better match
                    if (match.Confidence > existing.Confidence)
                    {
                        var tracked = await db.EpgChannelMappings.FindAsync(
                            [existing.EpgChannelMappingId], cancellationToken);
                        if (tracked is not null)
                        {
                            tracked.XmltvChannelId = match.Channel.XmltvChannelId;
                            tracked.MappingMode = match.Mode;
                            tracked.Confidence = match.Confidence;
                            tracked.UpdatedUtc = now;
                            updated++;
                        }
                    }
                }
                else
                {
                    db.EpgChannelMappings.Add(new EpgChannelMapping
                    {
                        EpgChannelMappingId = Guid.NewGuid().ToString(),
                        ProfileId = profileId,
                        ProviderChannelId = channel.ProviderChannelId,
                        EpgSourceId = catalogue.SourceId,
                        XmltvChannelId = match.Channel.XmltvChannelId,
                        MappingMode = match.Mode,
                        Confidence = match.Confidence,
                        CreatedUtc = now,
                        UpdatedUtc = now,
                    });
                    created++;
                }
            }
        }

        if (created > 0 || updated > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("EPG auto-map: {Created} created, {Updated} updated for profile {ProfileId}.",
                created, updated, profileId);
        }
    }
}
