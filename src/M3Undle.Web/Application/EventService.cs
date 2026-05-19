using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public sealed class EventService(
    IServiceScopeFactory scopeFactory,
    AppEventBus eventBus) : IEventService
{
    // SQLite allows one writer at a time; serialise all writes.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task PublishAsync(
        SystemEventSeverity severity,
        string eventType,
        string title,
        string? detail = null,
        string? providerId = null,
        string? integrationId = null)
    {
        await _writeLock.WaitAsync();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            if (eventType == SystemEventTypes.ProviderFetchFailed && providerId is not null)
            {
                await db.SystemEvents
                    .Where(e => e.EventType == SystemEventTypes.ProviderBackOnline && e.ProviderId == providerId)
                    .ExecuteDeleteAsync(CancellationToken.None);
            }

            if (eventType == SystemEventTypes.ProviderStreamUnstable && providerId is not null)
            {
                await db.SystemEvents
                    .Where(e => e.EventType == SystemEventTypes.ProviderStreamRecovered && e.ProviderId == providerId)
                    .ExecuteDeleteAsync(CancellationToken.None);
            }

            if (eventType == SystemEventTypes.ProviderStreamRecovered && providerId is not null)
            {
                await db.SystemEvents
                    .Where(e => e.EventType == SystemEventTypes.ProviderStreamUnstable && e.ProviderId == providerId)
                    .ExecuteDeleteAsync(CancellationToken.None);
            }

            SystemEvent? existing = null;
            if (providerId is not null)
                existing = await db.SystemEvents.FirstOrDefaultAsync(
                    e => e.EventType == eventType && e.ProviderId == providerId);
            else if (integrationId is not null)
                existing = await db.SystemEvents.FirstOrDefaultAsync(
                    e => e.EventType == eventType && e.IntegrationId == integrationId);

            if (existing is not null)
            {
                existing.OccurrenceCount++;
                existing.OccurredAt = DateTime.UtcNow;
                existing.Title = title;
                existing.Detail = detail;
                db.Update(existing);
            }
            else
            {
                db.SystemEvents.Add(new SystemEvent
                {
                    SystemEventId = Guid.NewGuid().ToString(),
                    EventType = eventType,
                    Severity = severity.ToString(),
                    Title = title,
                    Detail = detail,
                    ProviderId = providerId,
                    IntegrationId = integrationId,
                    OccurredAt = DateTime.UtcNow,
                    OccurrenceCount = 1,
                });
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
        finally
        {
            _writeLock.Release();
        }

        eventBus.Publish(AppEventKind.SystemEventPublished);
    }

    public async Task<IReadOnlyList<SystemEvent>> GetAllAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.SystemEvents
            .AsNoTracking()
            .OrderByDescending(e => e.OccurredAt)
            .ToListAsync(ct);
    }

    public async Task<int> GetCountAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.SystemEvents.AsNoTracking().CountAsync(ct);
    }

    public async Task<SystemEventSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var severities = await db.SystemEvents
            .AsNoTracking()
            .Select(e => e.Severity)
            .ToListAsync(ct);

        if (severities.Count == 0)
            return new SystemEventSummary(0, null);

        var highest = SystemEventSeverity.Info;
        foreach (var severity in severities)
        {
            if (!Enum.TryParse<SystemEventSeverity>(severity, ignoreCase: true, out var parsed))
                continue;

            if (SeverityRank(parsed) > SeverityRank(highest))
                highest = parsed;
        }

        return new SystemEventSummary(severities.Count, highest);
    }

    public async Task DismissAsync(string eventId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.SystemEvents
                .Where(e => e.SystemEventId == eventId)
                .ExecuteDeleteAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }

        eventBus.Publish(AppEventKind.SystemEventPublished);
    }

    public async Task DismissAllAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.SystemEvents.ExecuteDeleteAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }

        eventBus.Publish(AppEventKind.SystemEventPublished);
    }

    public async Task<bool> HasEventAsync(string eventType, string? providerId = null, string? integrationId = null, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        IQueryable<SystemEvent> query = db.SystemEvents.AsNoTracking()
            .Where(e => e.EventType == eventType);

        if (providerId is not null)
            query = query.Where(e => e.ProviderId == providerId);
        else if (integrationId is not null)
            query = query.Where(e => e.IntegrationId == integrationId);

        return await query.AnyAsync(ct);
    }

    public async Task<int> GetRetentionDaysAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = await db.SiteSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        return NormalizeRetentionDays(settings?.EventRetentionDays);
    }

    public async Task SetRetentionDaysAsync(int days, CancellationToken ct = default)
    {
        if (days is < SystemEventSettings.MinRetentionDays or > SystemEventSettings.MaxRetentionDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(days),
                days,
                $"Event retention days must be between {SystemEventSettings.MinRetentionDays} and {SystemEventSettings.MaxRetentionDays}.");
        }

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = await db.SiteSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
            if (settings is not null)
            {
                settings.EventRetentionDays = days;
                await db.SaveChangesAsync(ct);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task CleanupOldEventsAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var settings = await db.SiteSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
            var retentionDays = NormalizeRetentionDays(settings?.EventRetentionDays);
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            var deleted = await db.SystemEvents
                .Where(e => e.OccurredAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
                eventBus.Publish(AppEventKind.SystemEventPublished);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static int SeverityRank(SystemEventSeverity severity) => severity switch
    {
        SystemEventSeverity.Error => 3,
        SystemEventSeverity.Warning => 2,
        _ => 1,
    };

    private static int NormalizeRetentionDays(int? days)
        => days is >= SystemEventSettings.MinRetentionDays and <= SystemEventSettings.MaxRetentionDays
            ? days.Value
            : SystemEventSettings.DefaultRetentionDays;
}
