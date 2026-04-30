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

    public Task<EffectiveRefreshScheduleSettings> GetEffectiveSettingsAsync(string profileId, CancellationToken ct = default)
        => Task.FromResult(new EffectiveRefreshScheduleSettings(
            profileId,
            new RefreshScheduleSettings("manual", false),
            UsesProfileOverride: false,
            new RefreshScheduleSettings("manual", false)));

    public Task<EffectiveRefreshScheduleSettings?> GetActiveProfileSettingsAsync(CancellationToken ct = default)
        => Task.FromResult<EffectiveRefreshScheduleSettings?>(new EffectiveRefreshScheduleSettings(
            "profile-1",
            new RefreshScheduleSettings("manual", false),
            UsesProfileOverride: false,
            new RefreshScheduleSettings("manual", false)));

    public Task<(bool Succeeded, string? Error)> UpdateProfileAsync(
        string profileId,
        ProfileRefreshScheduleSettings settings,
        CancellationToken ct = default)
        => Task.FromResult((true, (string?)null));

    public Task<DateTime?> GetNextScheduledRefreshUtcAsync(string profileId, CancellationToken ct = default)
        => Task.FromResult((DateTime?)null);
}
