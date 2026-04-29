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

        if (refreshTrigger.IsRefreshing)
            reasons.Add("refresh in progress");

        if (reasons.Count == 0)
            return HealthCheckResult.Healthy("ready", new Dictionary<string, object> { ["ready"] = true });

        return HealthCheckResult.Unhealthy(
            "not ready",
            data: new Dictionary<string, object>
            {
                ["ready"] = false,
                ["reasons"] = reasons,
            });
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

        object payload = report.Status == HealthStatus.Healthy
            ? new { ready = true, status = report.Status.ToString() }
            : new { ready = false, status = report.Status.ToString(), reasons };

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
}
