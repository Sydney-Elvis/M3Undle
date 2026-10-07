namespace M3Undle.Web.Application.Notifications;

public enum NotificationLifecycle
{
    /// <summary>Has an incident with opening, reminders and recovery.</summary>
    Incident,

    /// <summary>A single occurrence with no reminder or recovery.</summary>
    OneTime,
}

public static class NotificationSubjectKinds
{
    public const string EpgSource = "EpgSource";
    public const string Provider = "Provider";
    public const string Channel = "Channel";
    public const string DownstreamIntegration = "DownstreamIntegration";
    public const string Profile = "Profile";
    public const string Account = "Account";
    public const string System = "System";
}

public static class NotificationKeys
{
    public const string EpgFetchFailed = "epg.fetch_failed";
    public const string EpgRefreshOverdue = "epg.refresh_overdue";
    public const string EpgCoverageInsufficient = "epg.coverage_insufficient";
    public const string ProviderFetchFailed = "provider.fetch_failed";
    public const string StreamUnstable = "stream.unstable";
    public const string DownstreamRefreshFailed = "downstream.refresh_failed";
    public const string LineupBreakingChange = "lineup.breaking_change";
    public const string SecurityLoginFailed = "security.login_failed";
    public const string SecurityAccountLocked = "security.account_locked";
    public const string SystemRestarted = "system.restarted";
    public const string SystemMigrationsApplied = "system.migrations_applied";
    public const string SeriesSyncCompleted = "series.sync_completed";
}

/// <summary>
/// One configurable row. The key is an application concept; it is never a Matrix message type or an SMTP subject.
/// </summary>
public sealed record NotificationDefinition(
    string Key,
    string Label,
    string Trigger,
    string SubjectKind,
    NotificationLifecycle Lifecycle,
    string DefaultSeverity,
    IReadOnlyList<string> LegacyEventTypes,
    bool ProducerAvailable,
    TimeSpan? FixedSustainedDelay = null)
{
    public bool SupportsRecovery => Lifecycle == NotificationLifecycle.Incident;
    public bool SupportsReminders => Lifecycle == NotificationLifecycle.Incident;
}

/// <summary>
/// Immutable catalog of administrator notifications. Failure and recovery are one row and one route.
/// A row is only selectable once its producer package has passed; the page lists the whole catalog regardless.
/// </summary>
public static class NotificationCatalog
{
    public static IReadOnlyList<NotificationDefinition> Definitions { get; } =
    [
        new(NotificationKeys.EpgFetchFailed, "EPG source fetch failing",
            "A real fetch of a guide source has kept failing past the configured delay. Recovers on the next successful or unchanged check.",
            NotificationSubjectKinds.EpgSource, NotificationLifecycle.Incident, "Warning",
            [Application.SystemEventTypes.EpgFetchFailed, Application.SystemEventTypes.EpgBackOnline], ProducerAvailable: true),
        new(NotificationKeys.EpgRefreshOverdue, "EPG refresh overdue",
            "A guide source was expected to be checked on its schedule but no real check completed within the grace period.",
            NotificationSubjectKinds.EpgSource, NotificationLifecycle.Incident, "Warning", [], ProducerAvailable: true),
        new(NotificationKeys.EpgCoverageInsufficient, "EPG future coverage insufficient",
            "Too few relevant channels have guide data for the coming hours, whether or not fetching succeeds.",
            NotificationSubjectKinds.EpgSource, NotificationLifecycle.Incident, "Warning", [], ProducerAvailable: true),
        new(NotificationKeys.ProviderFetchFailed, "Provider fetch failing",
            "A provider playlist refresh failed. Recovers when a later refresh commits successfully.",
            NotificationSubjectKinds.Provider, NotificationLifecycle.Incident, "Error",
            [Application.SystemEventTypes.ProviderFetchFailed, Application.SystemEventTypes.ProviderBackOnline], ProducerAvailable: true),
        new(NotificationKeys.StreamUnstable, "Sustained stream instability",
            "A channel stayed unhealthy with repeated upstream failures. Recovers after sustained healthy output for that channel.",
            NotificationSubjectKinds.Channel, NotificationLifecycle.Incident, "Warning",
            [Application.SystemEventTypes.ProviderStreamUnstable, Application.SystemEventTypes.ProviderStreamRecovered], ProducerAvailable: true,
            FixedSustainedDelay: TimeSpan.FromMinutes(2)),
        new(NotificationKeys.DownstreamRefreshFailed, "Downstream refresh failing",
            "The last command sent to a Jellyfin, Emby or webhook integration failed. Recovers after a later success.",
            NotificationSubjectKinds.DownstreamIntegration, NotificationLifecycle.Incident, "Warning",
            [Application.SystemEventTypes.DownstreamNotificationFailed], ProducerAvailable: true),
        new(NotificationKeys.LineupBreakingChange, "Breaking lineup change",
            "A published lineup change was classified as breaking for downstream clients.",
            NotificationSubjectKinds.Profile, NotificationLifecycle.OneTime, "Warning",
            [Application.SystemEventTypes.BreakingLineupChange, Application.SystemEventTypes.ProviderLineupMassChange,
             Application.SystemEventTypes.ProviderFetchSuspect], ProducerAvailable: true),
        new(NotificationKeys.SecurityLoginFailed, "Failed sign-in attempts",
            "Failed sign-ins, summarised per account in five-minute windows. Entered identifiers, addresses and user agents are never sent.",
            NotificationSubjectKinds.Account, NotificationLifecycle.OneTime, "Warning",
            [Application.SystemEventTypes.LoginFailed], ProducerAvailable: true),
        new(NotificationKeys.SecurityAccountLocked, "Account locked",
            "An account was locked after repeated failed sign-ins.",
            NotificationSubjectKinds.Account, NotificationLifecycle.OneTime, "Warning",
            [Application.SystemEventTypes.AccountLocked], ProducerAvailable: true),
        new(NotificationKeys.SystemRestarted, "Application restarted",
            "M3Undle started, after migrations and any staged restore were applied.",
            NotificationSubjectKinds.System, NotificationLifecycle.OneTime, "Info",
            [Application.SystemEventTypes.AppRestarted], ProducerAvailable: true),
        new(NotificationKeys.SystemMigrationsApplied, "Database migrations applied",
            "Database migrations were applied during startup.",
            NotificationSubjectKinds.System, NotificationLifecycle.OneTime, "Info",
            [Application.SystemEventTypes.DatabaseMigrationApplied], ProducerAvailable: true),
        new(NotificationKeys.SeriesSyncCompleted, "Series synchronization completed",
            "A series synchronization run finished. Can be noisy; off by default.",
            NotificationSubjectKinds.System, NotificationLifecycle.OneTime, "Info",
            [Application.SystemEventTypes.SeriesSyncCompleted], ProducerAvailable: true),
    ];

    private static readonly Dictionary<string, NotificationDefinition> ByKey =
        Definitions.ToDictionary(x => x.Key, StringComparer.Ordinal);

    public static NotificationDefinition? Find(string? key)
        => key is not null && ByKey.TryGetValue(key, out var definition) ? definition : null;

    public static bool IsKnown(string? key) => Find(key) is not null;
}
