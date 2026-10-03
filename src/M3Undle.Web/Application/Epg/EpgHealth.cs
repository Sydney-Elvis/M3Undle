using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Epg;

/// <summary>
/// An enabled EPG source whose most recent fetch failed. While this is true the guide is being
/// served from the last cached payload, so it silently goes stale.
/// </summary>
public sealed record FailingEpgSource(
    string EpgSourceId,
    string SourceName,
    string? ProviderId,
    string? ProviderName,
    DateTime? LastSuccessUtc,
    DateTime LastFailureUtc,
    string? ErrorSummary);

public static class EpgHealth
{
    /// <summary>
    /// Returns enabled sources whose latest attempt failed (a failure newer than the last success).
    /// Optionally restricted to <paramref name="providerIds"/>.
    /// </summary>
    public static async Task<List<FailingEpgSource>> GetFailingSourcesAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<string>? providerIds,
        CancellationToken cancellationToken)
    {
        var query = db.EpgSources
            .AsNoTracking()
            .Where(s => s.Enabled
                        && s.LastFailureUtc != null
                        && (s.LastSuccessUtc == null || s.LastFailureUtc > s.LastSuccessUtc));

        if (providerIds is not null)
            query = query.Where(s => s.ProviderId != null && providerIds.Contains(s.ProviderId));

        var failing = await query
            .Select(s => new
            {
                s.EpgSourceId,
                s.Name,
                s.ProviderId,
                ProviderName = s.Provider != null ? s.Provider.Name : null,
                s.LastSuccessUtc,
                LastFailureUtc = s.LastFailureUtc!.Value,
            })
            .ToListAsync(cancellationToken);

        if (failing.Count == 0)
            return [];

        var ids = failing.Select(f => f.EpgSourceId).ToList();
        var runs = await db.EpgFetchRuns
            .AsNoTracking()
            .Where(r => ids.Contains(r.EpgSourceId) && r.Status == "fail" && r.ErrorSummary != null)
            .Select(r => new { r.EpgSourceId, r.StartedUtc, r.ErrorSummary })
            .ToListAsync(cancellationToken);

        var errors = runs
            .GroupBy(r => r.EpgSourceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartedUtc).First().ErrorSummary);

        return failing
            .Select(f => new FailingEpgSource(
                f.EpgSourceId,
                f.Name,
                f.ProviderId,
                f.ProviderName,
                f.LastSuccessUtc,
                f.LastFailureUtc,
                errors.GetValueOrDefault(f.EpgSourceId)))
            .ToList();
    }

    public static string DescribeStale(DateTime? lastSuccessUtc, DateTime nowUtc)
    {
        if (lastSuccessUtc is null)
            return "never fetched successfully";

        var age = nowUtc - lastSuccessUtc.Value;
        return age.TotalHours < 1 ? "last updated less than an hour ago"
            : age.TotalHours < 48 ? $"last updated {(int)age.TotalHours}h ago"
            : $"last updated {(int)age.TotalDays}d ago";
    }
}
