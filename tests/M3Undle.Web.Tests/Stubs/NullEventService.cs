using M3Undle.Web.Application;
using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Tests.Stubs;

public sealed class NullEventService : IEventService
{
    public Task PublishAsync(SystemEventSeverity severity, string eventType, string title, string? detail = null, string? providerId = null, string? integrationId = null)
        => Task.CompletedTask;

    public Task<IReadOnlyList<SystemEvent>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SystemEvent>>([]);

    public Task<int> GetCountAsync(CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<SystemEventSummary> GetSummaryAsync(CancellationToken ct = default)
        => Task.FromResult(new SystemEventSummary(0, null));

    public Task DismissAsync(string eventId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DismissAllAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public Task CleanupOldEventsAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> HasEventAsync(string eventType, string? providerId = null, string? integrationId = null, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<int> GetRetentionDaysAsync(CancellationToken ct = default)
        => Task.FromResult(7);

    public Task SetRetentionDaysAsync(int days, CancellationToken ct = default)
        => Task.CompletedTask;
}
