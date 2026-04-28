using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Application;

public enum SystemEventSeverity { Info, Warning, Error }

public sealed record SystemEventSummary(int Count, SystemEventSeverity? HighestSeverity);

public static class SystemEventSettings
{
    public const int DefaultRetentionDays = 7;
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 365;
}

public static class SystemEventTypes
{
    public const string ProviderFetchFailed = "ProviderFetchFailed";
    public const string ProviderBackOnline = "ProviderBackOnline";
    public const string ProviderStreamUnstable = "ProviderStreamUnstable";
    public const string ProviderStreamRecovered = "ProviderStreamRecovered";
    public const string BreakingLineupChange = "BreakingLineupChange";
    public const string DownstreamNotificationFailed = "DownstreamNotificationFailed";
    public const string LoginFailed = "LoginFailed";
    public const string AccountLocked = "AccountLocked";
    public const string AppRestarted = "AppRestarted";
    public const string DatabaseMigrationApplied = "DatabaseMigrationApplied";
}

public interface IEventService
{
    Task PublishAsync(SystemEventSeverity severity, string eventType, string title, string? detail = null, string? providerId = null, string? integrationId = null);
    Task<IReadOnlyList<SystemEvent>> GetAllAsync(CancellationToken ct = default);
    Task<int> GetCountAsync(CancellationToken ct = default);
    Task<SystemEventSummary> GetSummaryAsync(CancellationToken ct = default);
    Task DismissAsync(string eventId, CancellationToken ct = default);
    Task DismissAllAsync(CancellationToken ct = default);
    Task CleanupOldEventsAsync(CancellationToken ct = default);
    Task<bool> HasEventAsync(string eventType, string? providerId = null, string? integrationId = null, CancellationToken ct = default);
    Task<int> GetRetentionDaysAsync(CancellationToken ct = default);
    Task SetRetentionDaysAsync(int days, CancellationToken ct = default);
}
