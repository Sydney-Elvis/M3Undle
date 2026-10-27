using System.Text.Json;

namespace M3Undle.Web.Application;

/// <summary>
/// Parses the episodes JSON persisted in <c>XtreamSeriesCache</c> (the raw get_series_info
/// response) into flat per-episode entries. Shared by the full-fetch path (XtreamLineupClient)
/// and the build-only DB-reconstruction path (SnapshotBuilder) so both build identical episode
/// channels from the same cached payload.
/// </summary>
internal static class XtreamEpisodeParser
{
    internal readonly record struct Episode(int EpisodeId, string DisplayName, string Extension);

    internal static List<Episode> Parse(string seriesName, string episodesJson)
    {
        var result = new List<Episode>();
        try
        {
            using var doc = JsonDocument.Parse(episodesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("episodes", out var episodesEl))
                return result;

            foreach (var (seasonName, seasonEpisodes) in EnumerateSeasons(episodesEl))
            {
                if (seasonEpisodes.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var ep in seasonEpisodes.EnumerateArray())
                {
                    var epId = ReadInt(ep, "id");
                    if (epId <= 0) continue;

                    var epTitle = ReadString(ep, "title") ?? string.Empty;
                    var ext = ReadString(ep, "container_extension") ?? "mkv";
                    var epNum = ReadInt(ep, "episode_num");

                    var episodeMarker = $"S{seasonName.PadLeft(2, '0')}E{epNum:D2}";
                    var displayName = string.IsNullOrWhiteSpace(epTitle)
                        ? $"{seriesName} {episodeMarker}"
                        : $"{seriesName} {episodeMarker} — {epTitle}";

                    result.Add(new Episode(epId, displayName, ext));
                }
            }
        }
        // Deliberately catches everything, not just JsonException: providers occasionally send
        // episode entries in shapes TryGetProperty can't handle (e.g. a season or episode value
        // that isn't an object), which throws InvalidOperationException rather than JsonException.
        // A single series with malformed data must degrade to "no episodes," never take down the
        // whole snapshot refresh.
        catch (Exception) { }
        return result;
    }

    // Xtream panels are PHP: episodes is normally an object keyed by season number, but PHP encodes
    // a map whose keys are exactly 0..n-1 as a JSON array, so a series with seasons 0-5 (or 0-n)
    // arrives as an array of per-season arrays. The array index is then the season number.
    private static IEnumerable<(string Season, JsonElement Episodes)> EnumerateSeasons(JsonElement episodes)
    {
        switch (episodes.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var season in episodes.EnumerateObject())
                    yield return (season.Name, season.Value);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var season in episodes.EnumerateArray())
                    yield return ((index++).ToString(), season);
                break;
        }
    }

    private static int ReadInt(JsonElement el, string property)
        => el.TryGetProperty(property, out var v) ? ReadInt(v) : 0;

    private static int ReadInt(JsonElement el)
        => el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt32(out var n) ? n : 0,
            JsonValueKind.String => int.TryParse(el.GetString(), out var n) ? n : 0,
            _ => 0
        };

    private static string? ReadString(JsonElement el, string property)
        => el.TryGetProperty(property, out var v) ? ReadString(v) : null;

    private static string? ReadString(JsonElement el)
        => el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
