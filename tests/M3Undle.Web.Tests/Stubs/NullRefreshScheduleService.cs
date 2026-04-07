using M3Undle.Web.Application;

namespace M3Undle.Web.Tests.Stubs;

/// <summary>Stub for tests — returns manual schedule with no next refresh time.</summary>
public sealed class NullRefreshScheduleService : IRefreshScheduleService
{
    public Task<RefreshScheduleSettings> GetSettingsAsync(CancellationToken ct = default)
        => Task.FromResult(new RefreshScheduleSettings("manual", false));

    public Task<(bool Succeeded, string? Error)> UpdateAsync(RefreshScheduleSettings settings, CancellationToken ct = default)
        => Task.FromResult((true, (string?)null));

    public Task<DateTime?> GetNextScheduledRefreshUtcAsync(CancellationToken ct = default)
        => Task.FromResult((DateTime?)null);
}
