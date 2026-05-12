using System.Text.Json;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace M3Undle.Web.Observability;

public sealed class M3UndleReadinessHealthCheck(
    IServiceScopeFactory scopeFactory,
    IRefreshTrigger refreshTrigger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var reasons = new List<string>();

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (!await db.Database.CanConnectAsync(cancellationToken))
                reasons.Add("database unavailable");

            var activeProfileId = await db.Profiles.AsNoTracking()
                .Where(x => x.IsActive && x.Enabled)
                .Select(x => x.ProfileId)
                .FirstOrDefaultAsync(cancellationToken);

            if (activeProfileId is null)
            {
                reasons.Add("no active profile");
            }
            else
            {
                var hasActiveSnapshot = await db.Snapshots.AsNoTracking()
                    .AnyAsync(x => x.ProfileId == activeProfileId && x.Status == "active", cancellationToken);
                if (!hasActiveSnapshot)
                    reasons.Add("no active snapshot for active profile");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            reasons.Add($"database unavailable: {ex.Message}");
        }

        if (refreshTrigger.IsRefreshing && reasons.Count > 0)
            reasons.Add("refresh in progress");

        if (reasons.Count == 0)
            return HealthCheckResult.Healthy("ready", new Dictionary<string, object> { ["ready"] = true });

        var reason = string.Join("; ", reasons);

        return HealthCheckResult.Unhealthy(
            $"not ready: {reason}",
            data: new Dictionary<string, object>
            {
                ["ready"] = false,
                ["reason"] = reason,
                ["reasons"] = new ReadinessReasons(reasons),
            });
    }

    private sealed class ReadinessReasons(IReadOnlyList<string> reasons) : IReadOnlyList<string>
    {
        public int Count => reasons.Count;

        public string this[int index] => reasons[index];

        public IEnumerator<string> GetEnumerator() => reasons.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => string.Join("; ", reasons);
    }
}

public static class M3UndleHealthResponseWriters
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static Task WriteReadinessAsync(HttpContext context, HealthReport report)
    {
        var reasons = report.Entries.Values
            .SelectMany(entry => TryGetReasons(entry.Data))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var reason = string.Join("; ", reasons);

        object payload = report.Status == HealthStatus.Healthy
            ? new { ready = true, status = report.Status.ToString() }
            : new { ready = false, status = report.Status.ToString(), reason, reasons };

        context.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(context.Response.Body, payload, JsonOptions);
    }

    public static Task WriteHealthSummaryAsync(HttpContext context, HealthReport report)
    {
        var entries = report.Entries.ToDictionary(
            x => x.Key,
            x => new
            {
                status = x.Value.Status.ToString(),
                description = x.Value.Description,
                reason = TryGetReason(x.Value.Data),
                reasons = TryGetReasons(x.Value.Data),
                durationMilliseconds = x.Value.Duration.TotalMilliseconds,
            },
            StringComparer.Ordinal);

        var payload = new
        {
            status = report.Status.ToString(),
            healthy = report.Status == HealthStatus.Healthy,
            durationMilliseconds = report.TotalDuration.TotalMilliseconds,
            entries,
        };

        context.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(context.Response.Body, payload, JsonOptions);
    }

    private static IEnumerable<string> TryGetReasons(IReadOnlyDictionary<string, object> data)
    {
        if (!data.TryGetValue("reasons", out var value))
            yield break;

        if (value is IEnumerable<string> strings)
        {
            foreach (var reason in strings)
                yield return reason;
        }
    }

    private static string? TryGetReason(IReadOnlyDictionary<string, object> data)
        => data.TryGetValue("reason", out var value) && value is string reason
            ? reason
            : null;
}
