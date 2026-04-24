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

            // Build normalised lookup structures for this source
            var byExactId = sourceChannels
                .GroupBy(c => c.XmltvChannelId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var byNormName = sourceChannels
                .GroupBy(c => NormalizeName(c.DisplayName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var channel in providerChannels)
            {
                var key = (channel.ProviderChannelId, catalogue.SourceId);

                // Never overwrite manual mappings
                if (manualKeys.Contains(key))
                    continue;

                var (match, mode, confidence) = FindBestMatch(channel, byExactId, byNormName, sourceChannels);
                if (match is null)
                    continue;

                if (autoKeys.TryGetValue(key, out var existing))
                {
                    // Update only if we have a better match
                    if (confidence > existing.Confidence)
                    {
                        var tracked = await db.EpgChannelMappings.FindAsync(
                            [existing.EpgChannelMappingId], cancellationToken);
                        if (tracked is not null)
                        {
                            tracked.XmltvChannelId = match.XmltvChannelId;
                            tracked.MappingMode = mode;
                            tracked.Confidence = confidence;
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
                        XmltvChannelId = match.XmltvChannelId,
                        MappingMode = mode,
                        Confidence = confidence,
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

    // -------------------------------------------------------------------------
    // Matching
    // -------------------------------------------------------------------------

    private static (EpgChannelRecord? Match, string Mode, float Confidence) FindBestMatch(
        ProviderChannel channel,
        Dictionary<string, EpgChannelRecord> byExactId,
        Dictionary<string, EpgChannelRecord> byNormName,
        IReadOnlyList<EpgChannelRecord> allChannels)
    {
        // 1. Exact tvg-id match
        if (!string.IsNullOrWhiteSpace(channel.TvgId) &&
            byExactId.TryGetValue(channel.TvgId, out var exactMatch))
            return (exactMatch, "auto_id", 1.0f);

        // 2. Normalised display-name match
        var normName = NormalizeName(channel.DisplayName);
        if (!string.IsNullOrWhiteSpace(normName) &&
            byNormName.TryGetValue(normName, out var nameMatch))
            return (nameMatch, "auto_name", 0.9f);

        // Also try tvg-name
        if (!string.IsNullOrWhiteSpace(channel.TvgName))
        {
            var normTvgName = NormalizeName(channel.TvgName);
            if (!string.IsNullOrWhiteSpace(normTvgName) &&
                byNormName.TryGetValue(normTvgName, out var tvgNameMatch))
                return (tvgNameMatch, "auto_name", 0.9f);
        }

        // 3. Fuzzy token overlap (Jaccard ≥ 0.6)
        var channelTokens = Tokenize(channel.DisplayName);
        if (channelTokens.Count == 0)
            return (null, string.Empty, 0f);

        EpgChannelRecord? bestFuzzy = null;
        float bestScore = 0f;
        foreach (var source in allChannels)
        {
            var sourceTokens = Tokenize(source.DisplayName);
            if (sourceTokens.Count == 0)
                continue;

            var intersection = channelTokens.Intersect(sourceTokens, StringComparer.OrdinalIgnoreCase).Count();
            var union = channelTokens.Union(sourceTokens, StringComparer.OrdinalIgnoreCase).Count();
            var score = union == 0 ? 0f : (float)intersection / union;

            if (score >= 0.6f && score > bestScore)
            {
                bestScore = score;
                bestFuzzy = source;
            }
        }

        return bestFuzzy is not null
            ? (bestFuzzy, "auto_fuzzy", bestScore)
            : (null, string.Empty, 0f);
    }

    // -------------------------------------------------------------------------
    // Normalisation helpers
    // -------------------------------------------------------------------------

    internal static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Lowercase; treat non-alphanumeric chars (punctuation, whitespace) as word
        // separators so "CNN-US!" → "cnn us" rather than "cnnus".
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    internal static IReadOnlyList<string> Tokenize(string? value)
    {
        var norm = NormalizeName(value);
        if (string.IsNullOrWhiteSpace(norm))
            return [];
        return norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
