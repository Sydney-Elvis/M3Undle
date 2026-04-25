namespace M3Undle.Core.Epg;

public static class EpgChannelMatcher
{
    public static EpgChannelMatch? FindBestMatch(
        EpgChannelMatchCandidate channel,
        IReadOnlyList<EpgChannelRecord> sourceChannels)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(sourceChannels);

        if (sourceChannels.Count == 0)
        {
            return null;
        }

        var byExactId = sourceChannels
            .GroupBy(c => c.XmltvChannelId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var byNormName = sourceChannels
            .GroupBy(c => NormalizeName(c.DisplayName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return FindBestMatch(channel, byExactId, byNormName, sourceChannels);
    }

    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Lowercase; treat non-alphanumeric chars as word separators so
        // "CNN-US!" becomes "cnn us" rather than "cnnus".
        var normalized = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                normalized.Append(char.ToLowerInvariant(c));
            }
            else if (normalized.Length > 0 && normalized[^1] != ' ')
            {
                normalized.Append(' ');
            }
        }

        return normalized.ToString().Trim();
    }

    public static IReadOnlyList<string> Tokenize(string? value)
    {
        var normalized = NormalizeName(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static EpgChannelMatch? FindBestMatch(
        EpgChannelMatchCandidate channel,
        Dictionary<string, EpgChannelRecord> byExactId,
        Dictionary<string, EpgChannelRecord> byNormName,
        IReadOnlyList<EpgChannelRecord> allChannels)
    {
        if (!string.IsNullOrWhiteSpace(channel.TvgId) &&
            byExactId.TryGetValue(channel.TvgId, out var exactMatch))
        {
            return new EpgChannelMatch(exactMatch, "auto_id", 1.0f);
        }

        var normalizedName = NormalizeName(channel.DisplayName);
        if (!string.IsNullOrWhiteSpace(normalizedName) &&
            byNormName.TryGetValue(normalizedName, out var nameMatch))
        {
            return new EpgChannelMatch(nameMatch, "auto_name", 0.9f);
        }

        if (!string.IsNullOrWhiteSpace(channel.TvgName))
        {
            var normalizedTvgName = NormalizeName(channel.TvgName);
            if (!string.IsNullOrWhiteSpace(normalizedTvgName) &&
                byNormName.TryGetValue(normalizedTvgName, out var tvgNameMatch))
            {
                return new EpgChannelMatch(tvgNameMatch, "auto_name", 0.9f);
            }
        }

        var channelTokens = Tokenize(channel.DisplayName);
        if (channelTokens.Count == 0)
        {
            return null;
        }

        EpgChannelRecord? bestFuzzy = null;
        var bestScore = 0f;
        foreach (var source in allChannels)
        {
            var sourceTokens = Tokenize(source.DisplayName);
            if (sourceTokens.Count == 0)
            {
                continue;
            }

            var intersection = channelTokens.Intersect(sourceTokens, StringComparer.OrdinalIgnoreCase).Count();
            var union = channelTokens.Union(sourceTokens, StringComparer.OrdinalIgnoreCase).Count();
            var score = union == 0 ? 0f : (float)intersection / union;

            if (score >= 0.6f && score > bestScore)
            {
                bestScore = score;
                bestFuzzy = source;
            }
        }

        return bestFuzzy is null
            ? null
            : new EpgChannelMatch(bestFuzzy, "auto_fuzzy", bestScore);
    }
}
